using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Executa uma auditoria de site: sonda -> agente -> validacao -> persistencia.
///
/// Repete a forma de <see cref="ExecuteResearchRunUseCase"/> - o proprio
/// comentario daquele caso de uso previa isto -, com uma diferenca estrutural: a
/// SONDA RODA ANTES, e o resultado dela entra no prompt.
///
/// A ordem importa por dois motivos:
///
/// 1. **Site fora do ar encerra o run sem gastar modelo.** Dominio morto e um
///    resultado da auditoria, e forte: ele reprova a conta em Technology Pain
///    sozinho. Perguntar ao agente o que ele achou de uma pagina que nao existe
///    seria pagar para receber alucinacao.
/// 2. **O agente ve o que foi medido.** Mostrar o TTFB real no prompt e o que
///    faz a diferenca entre "o site parece lento" - opiniao - e "o site leva
///    2,3s para o primeiro byte, e a vitrine tem 40 fotos na home" - leitura
///    sobre um fato.
/// </summary>
public sealed class ExecuteWebsiteAuditUseCase(
    IAccountRepository accounts,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IWebsiteProbe probe,
    IWebsiteAuditPersister persister,
    IAgentRuntime runtime,
    IStructuredOutputValidator validator,
    IWebsiteAuditPromptBuilder prompts,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<ExecuteWebsiteAuditUseCase> logger)
{
    public async Task ExecuteAsync(OutboxEvent evt, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Deserialize<AuditRequestedPayload>(evt.PayloadJson)
            ?? throw new InvalidOperationException($"Payload invalido no evento {evt.Id}.");

        var context = await accounts.GetContextAsync(payload.AccountId, ct)
            ?? throw new InvalidOperationException($"Contexto da conta {payload.AccountId} indisponivel.");

        var url = payload.Url ?? NormalizeDomain(context.Domain);

        if (string.IsNullOrWhiteSpace(url))
        {
            // Sem dominio nao ha o que auditar. Isso e falha de PRE-CONDICAO, e
            // nao do agente: quem descobre o dominio e o Researcher. Falhar aqui
            // sem custo e melhor que mandar o agente procurar - ele acharia
            // qualquer site parecido e auditaria a empresa errada.
            await FailAsync(payload, clock.UtcNow, AgentRunResult.Failure("Conta sem dominio."),
                "missing_domain",
                [new SchemaViolation("/domain", "Conta nao tem dominio; rode a pesquisa antes da auditoria.")],
                ct);

            throw new InvalidOperationException(
                $"Conta {payload.AccountId} nao tem dominio para auditar.");
        }

        var startedAt = clock.UtcNow;

        // 1. Medicao. Deterministica, sem custo, e nunca lanca por site fora do ar.
        var measurement = await probe.ProbeAsync(url, ct);

        logger.LogInformation(
            "Sonda {Probe} em {Url}: alcancado={Reached}, status={Status}, ttfb={Ttfb}ms",
            probe.Name, url, measurement.Reached, measurement.StatusCode,
            measurement.TimeToFirstByte?.TotalMilliseconds);

        // 2. Site inalcancavel encerra aqui, com a auditoria que existe.
        if (!measurement.Reached)
        {
            await persister.PersistAsync(new WebsiteAuditPersistRequest
            {
                AccountId = payload.AccountId,
                ResearchRunId = payload.ResearchRunId,
                OutboxEventId = evt.Id,
                Probe = measurement,
                Profile = null,
                Score = WebsiteAuditScoring.Calculate(measurement),
                AgentRun = BuildAgentRun(
                    payload, startedAt,
                    new AgentRunResult { RawText = string.Empty, Succeeded = true },
                    RunStatus.Completed, null)
            }, ct);

            logger.LogWarning(
                "Auditoria da conta {AccountId} concluida sem agente: {Url} inalcancavel ({Error}).",
                payload.AccountId, url, measurement.Error ?? measurement.StatusCode?.ToString());

            return;
        }

        // 3. Passada do agente.
        var systemPrompt = prompts.BuildSystemPrompt();

        var result = await runtime.RunAsync(new AgentRunRequest
        {
            AgentName = prompts.AgentName,
            PromptVersion = prompts.PromptVersion,
            SystemPrompt = systemPrompt,
            UserPrompt = prompts.BuildUserPrompt(context, measurement),
            SessionId = payload.ResearchRunId.ToString(),
            FixtureScenario = payload.FixtureScenario,
            Metadata = new Dictionary<string, string>
            {
                ["account_id"] = payload.AccountId.ToString(),
                ["research_run_id"] = payload.ResearchRunId.ToString(),
                ["audited_url"] = url
            }
        }, ct);

        if (!result.Succeeded)
        {
            await FailAsync(payload, startedAt, result, "agent_runtime_error",
                [new SchemaViolation(string.Empty, result.Error ?? "Falha desconhecida no runtime.")], ct);

            throw new AgentRuntimeException(result.Error ?? "Runtime de agente falhou.");
        }

        var outcome = validator.Validate<WebsiteAuditProfile>(result.RawText);
        var violations = outcome.IsValid
            ? EvidenceFirstGuard.Check(outcome.Value!)
            : outcome.Violations;

        // 4. Uma tentativa de reparo. Uma, e nao varias, pela mesma razao do
        //    Researcher: se o modelo nao acerta com os erros em maos, o problema
        //    e do prompt e insistir so queima orcamento.
        if (violations.Count > 0)
        {
            logger.LogWarning(
                "Output do Website Auditor rejeitado para a conta {AccountId}; tentando reparo. Violacoes: {Count}",
                payload.AccountId, violations.Count);

            var describe = string.Join("\n", violations.Select(v => $"- {v}"));

            var repaired = await runtime.RunAsync(new AgentRunRequest
            {
                AgentName = prompts.AgentName,
                PromptVersion = prompts.PromptVersion,
                SystemPrompt = systemPrompt,
                UserPrompt = prompts.BuildRepairPrompt(context, measurement, result.RawText, describe),
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

                outcome = validator.Validate<WebsiteAuditProfile>(result.RawText);
                violations = outcome.IsValid ? EvidenceFirstGuard.Check(outcome.Value!) : outcome.Violations;
            }
        }

        if (violations.Count > 0)
        {
            await FailAsync(payload, startedAt, result, "contract_violation", violations, ct);

            throw new InvalidOperationException(
                $"Auditoria rejeitada apos reparo: {violations.Count} violacao(oes).");
        }

        // 5. A nota e nossa. O agente entregou observacoes; a aritmetica sobre
        //    elas e da plataforma (ADR-0005).
        var score = WebsiteAuditScoring.Calculate(measurement, outcome.Value);

        await persister.PersistAsync(new WebsiteAuditPersistRequest
        {
            AccountId = payload.AccountId,
            ResearchRunId = payload.ResearchRunId,
            OutboxEventId = evt.Id,
            Probe = measurement,
            Profile = outcome.Value,
            Score = score,
            AgentRun = BuildAgentRun(payload, startedAt, result, RunStatus.Completed, null)
        }, ct);

        logger.LogInformation(
            "Auditoria concluida para a conta {AccountId}: cobertura {Coverage:P0}, " +
            "performance {Performance}, tracking {Tracking}",
            payload.AccountId, score.Coverage, score.Performance, score.Tracking);
    }

    /// <summary>
    /// Registra a falha fora de qualquer transacao de negocio: o custo do agente
    /// ja foi incorrido e o motivo precisa sobreviver ao rollback.
    ///
    /// Diferente do Researcher, aqui NAO ha transicao de status a desfazer: a
    /// auditoria nao move a conta na maquina de estados. Auditar e observar; a
    /// promocao de estado continua sendo da pesquisa e do score.
    /// </summary>
    private async Task FailAsync(
        AuditRequestedPayload payload,
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

        logger.LogError(
            "Auditoria falhou para a conta {AccountId} ({Reason}): {Violations}",
            payload.AccountId, reason, error);
    }

    private AgentRun BuildAgentRun(
        AuditRequestedPayload payload,
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

    /// <summary>
    /// <c>accounts.domain</c> guarda host puro ("grupoventosul.com.br"), do jeito
    /// que o Researcher o grava. A sonda precisa de URL absoluta.
    /// </summary>
    private static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var trimmed = domain.Trim();

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://{trimmed}";
    }
}

public sealed record AuditRequestedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("account_id")]
    public Guid AccountId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("research_run_id")]
    public Guid ResearchRunId { get; init; }

    /// <summary>
    /// URL explicita. Ausente, sai de <c>accounts.domain</c>. Existe para o caso
    /// de auditar uma vitrine em subdominio proprio - comum no setor, onde o
    /// institucional e o estoque as vezes moram separados.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string? Url { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("fixture_scenario")]
    public string? FixtureScenario { get; init; }
}
