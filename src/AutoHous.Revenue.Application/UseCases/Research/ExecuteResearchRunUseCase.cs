using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Executa um run de pesquisa: contexto -> agente -> validacao -> persistencia.
///
/// Este caso de uso e o vertical slice inteiro. Os agentes seguintes (Website
/// Auditor, Product Matcher, People Finder, SDR) repetem esta forma.
///
/// Vive na Application, e nao no Worker, porque nada aqui e especifico de "rodar
/// dentro de um BackgroundService": o Worker so entrega o evento. Consequencia
/// pratica: da para testar o ciclo de reparo inteiro com portas falsas, sem
/// Postgres e sem Hermes.
/// </summary>
public sealed class ExecuteResearchRunUseCase(
    IAccountRepository accounts,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IResearchProfilePersister persister,
    IUnitOfWorkFactory unitOfWorkFactory,
    IAgentRuntime runtime,
    IStructuredOutputValidator validator,
    IResearchPromptBuilder prompts,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<ExecuteResearchRunUseCase> logger)
{
    public async Task ExecuteAsync(OutboxEvent evt, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Deserialize<ResearchRequestedPayload>(evt.PayloadJson)
            ?? throw new InvalidOperationException($"Payload invalido no evento {evt.Id}.");

        var account = await accounts.GetAsync(payload.AccountId, ct)
            ?? throw new InvalidOperationException($"Conta {payload.AccountId} nao encontrada.");

        var context = await accounts.GetContextAsync(payload.AccountId, ct)
            ?? throw new InvalidOperationException($"Contexto da conta {payload.AccountId} indisponivel.");

        var startedAt = clock.UtcNow;
        var systemPrompt = prompts.BuildSystemPrompt();

        // 1. Primeira tentativa.
        var result = await runtime.RunAsync(new AgentRunRequest
        {
            AgentName = prompts.AgentName,
            PromptVersion = prompts.PromptVersion,
            SystemPrompt = systemPrompt,
            UserPrompt = prompts.BuildUserPrompt(context),
            SessionId = payload.ResearchRunId.ToString(),
            FixtureScenario = payload.FixtureScenario,
            Metadata = new Dictionary<string, string>
            {
                ["account_id"] = payload.AccountId.ToString(),
                ["research_run_id"] = payload.ResearchRunId.ToString()
            }
        }, ct);

        if (!result.Succeeded)
        {
            await FailAsync(payload, account, startedAt, result, "agent_runtime_error",
                [new SchemaViolation(string.Empty, result.Error ?? "Falha desconhecida no runtime.")], ct);

            throw new AgentRuntimeException(result.Error ?? "Runtime de agente falhou.");
        }

        var outcome = validator.Validate<ResearchProfile>(result.RawText);
        var violations = outcome.IsValid
            ? EvidenceFirstGuard.Check(outcome.Value!)
            : outcome.Violations;

        // 2. Uma tentativa de reparo, devolvendo as violacoes ao agente.
        if (violations.Count > 0)
        {
            logger.LogWarning(
                "Output do Researcher rejeitado para a conta {AccountId}; tentando reparo. Violacoes: {Count}",
                payload.AccountId, violations.Count);

            var describe = string.Join("\n", violations.Select(v => $"- {v}"));

            var repaired = await runtime.RunAsync(new AgentRunRequest
            {
                AgentName = prompts.AgentName,
                PromptVersion = prompts.PromptVersion,
                SystemPrompt = systemPrompt,
                UserPrompt = prompts.BuildRepairPrompt(context, result.RawText, describe),
                SessionId = payload.ResearchRunId.ToString(),
                FixtureScenario = $"{payload.FixtureScenario ?? "success"}-repaired",
                Metadata = new Dictionary<string, string>
                {
                    ["account_id"] = payload.AccountId.ToString(),
                    ["research_run_id"] = payload.ResearchRunId.ToString(),
                    ["repair_attempt"] = "1"
                }
            }, ct);

            if (repaired.Succeeded)
            {
                result = result with
                {
                    RawText = repaired.RawText,
                    InputTokens = (result.InputTokens ?? 0) + (repaired.InputTokens ?? 0),
                    OutputTokens = (result.OutputTokens ?? 0) + (repaired.OutputTokens ?? 0),
                    EstimatedCost = (result.EstimatedCost ?? 0) + (repaired.EstimatedCost ?? 0)
                };

                outcome = validator.Validate<ResearchProfile>(result.RawText);
                violations = outcome.IsValid ? EvidenceFirstGuard.Check(outcome.Value!) : outcome.Violations;
            }
        }

        if (violations.Count > 0)
        {
            await FailAsync(payload, account, startedAt, result, "contract_violation", violations, ct);

            // Lanca para que o outbox reagende com backoff (ou mate em dead-letter).
            throw new InvalidOperationException(
                $"Research profile rejeitado apos reparo: {violations.Count} violacao(oes).");
        }

        // 3. Persistencia transacional.
        await persister.PersistAsync(new ResearchProfilePersistRequest
        {
            AccountId = payload.AccountId,
            ResearchRunId = payload.ResearchRunId,
            OutboxEventId = evt.Id,
            CurrentStatus = account.Status,
            Profile = outcome.Value!,
            AgentRun = BuildAgentRun(payload, startedAt, result, RunStatus.Completed, null)
        }, ct);

        logger.LogInformation(
            "Pesquisa concluida para a conta {AccountId}: {Evidence} evidencia(s), completude {Completeness}",
            payload.AccountId, outcome.Value!.Evidence.Count, outcome.Value.ResearchCompleteness);
    }

    /// <summary>
    /// Registra a falha fora de qualquer transacao de negocio: o custo do agente
    /// ja foi incorrido e o motivo precisa sobreviver ao rollback.
    /// </summary>
    private async Task FailAsync(
        ResearchRequestedPayload payload,
        Account account,
        DateTimeOffset startedAt,
        AgentRunResult result,
        string reason,
        IReadOnlyList<SchemaViolation> violations,
        CancellationToken ct)
    {
        var error = JsonSerializer.Serialize(new
        {
            reason,
            violations = violations.Select(v => new { location = v.Location, message = v.Message })
        });

        await researchRuns.FailAsync(payload.ResearchRunId, error, ct);

        await agentRuns.InsertOutsideTransactionAsync(
            BuildAgentRun(payload, startedAt, result, RunStatus.Failed, error), ct);

        // Devolve a conta para discovered: ela nao foi pesquisada e precisa poder
        // ser reenfileirada.
        if (AccountStatusTransitions.CanTransition(account.Status, AccountStatus.Discovered))
        {
            await using var uow = await unitOfWorkFactory.BeginAsync(ct);
            await accounts.TransitionAsync(uow, account.Id, account.Status, AccountStatus.Discovered, ct);
            await uow.CommitAsync(ct);
        }

        logger.LogError(
            "Pesquisa falhou para a conta {AccountId} ({Reason}): {Violations}",
            payload.AccountId, reason, error);
    }

    private AgentRun BuildAgentRun(
        ResearchRequestedPayload payload,
        DateTimeOffset startedAt,
        AgentRunResult result,
        string status,
        string? errorJson) => new()
    {
        Id = ids.NewId(),
        AccountId = payload.AccountId,
        ResearchRunId = payload.ResearchRunId,
        AgentName = prompts.AgentName,
        PromptVersion = prompts.PromptVersion,
        ModelProvider = result.ModelProvider,
        ModelName = result.ModelName,
        ExternalRunId = result.ExternalRunId,
        Status = status,
        InputTokens = result.InputTokens,
        OutputTokens = result.OutputTokens,
        EstimatedCost = result.EstimatedCost,
        StartedAt = startedAt,
        FinishedAt = clock.UtcNow,
        ErrorJson = errorJson
    };
}

public sealed record ResearchRequestedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("account_id")]
    public Guid AccountId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("research_run_id")]
    public Guid ResearchRunId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("depth")]
    public string Depth { get; init; } = "standard";

    /// <summary>
    /// So tem efeito com AGENT_RUNTIME=fixture: escolhe qual resposta gravada
    /// usar. Permite que os testes exercitem sucesso, reparo e falha dura pelo
    /// mesmo caminho de codigo de producao.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("fixture_scenario")]
    public string? FixtureScenario { get; init; }
}
