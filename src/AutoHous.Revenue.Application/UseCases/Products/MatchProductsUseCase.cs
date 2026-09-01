using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Product Matcher (A04): calcula o fit, pede o argumento, valida e grava.
///
/// A ordem inverte a dos outros dois agentes, e a inversao e a decisao de
/// desenho desta entrega. No Researcher o agente vem primeiro e a plataforma
/// valida depois. Aqui a PLATAFORMA DECIDE PRIMEIRO - qual produto serve, com
/// quantos pontos e por quais criterios - e o agente recebe isso pronto.
///
/// Duas consequencias praticas:
///
/// 1. **O agente nao pode escolher errado**, porque nao escolhe. A pior falha
///    que ele consegue produzir e um argumento fraco para o produto certo, e
///    isso o ciclo de reparo pega.
/// 2. **A aritmetica sobrevive a falha do agente.** Se o modelo falhar de vez, o
///    fit calculado e gravado assim mesmo: a fila continua priorizada, so falta
///    a frase. O contrario - argumento sem nota - nao serviria para nada, porque
///    e a nota que decide a ordem da fila.
///
/// O ponto 2 e o que justifica <c>Pitch</c> ser anulavel no
/// <see cref="ProductFitPersistRequest"/>, e e o unico dos quatro agentes cuja
/// falha nao perde a etapa inteira.
/// </summary>
public sealed class MatchProductsUseCase(
    IAccountRepository accounts,
    IProductFitRepository fits,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IProductFitPersister persister,
    IAgentRuntime runtime,
    IStructuredOutputValidator validator,
    IProductPitchPromptBuilder prompts,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<MatchProductsUseCase> logger)
{
    public async Task ExecuteAsync(OutboxEvent evt, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Deserialize<MatchRequestedPayload>(evt.PayloadJson)
            ?? throw new InvalidOperationException($"Payload invalido no evento {evt.Id}.");

        var context = await accounts.GetContextAsync(payload.AccountId, ct)
            ?? throw new InvalidOperationException($"Contexto da conta {payload.AccountId} indisponivel.");

        var facts = await fits.LoadFactsAsync(payload.AccountId, ct)
            ?? throw new InvalidOperationException($"Fatos de fit indisponiveis para {payload.AccountId}.");

        var startedAt = clock.UtcNow;

        // 1. A aritmetica. Deterministica, sem custo, e nunca falha.
        var calculated = ProductFitScoring.Calculate(ToInputs(facts, startedAt));
        var entry = calculated.FirstOrDefault(f => f.RecommendedEntry);

        logger.LogInformation(
            "Fit calculado para a conta {AccountId}: entrada {Entry}, notas {Scores}",
            payload.AccountId,
            entry?.Product ?? "(nenhuma acima do corte)",
            string.Join(", ", calculated.Select(f => $"{f.Product}={f.Score:0}")));

        // 2. Passada do agente. So os produtos que valem argumento: pedir os
        //    cinco gastaria contexto escrevendo a defesa de um BoxTech que
        //    pontuou 12 - e o SDR nunca vai usar aquele texto.
        var worthPitching = Worthwhile(calculated);

        if (worthPitching.Count == 0)
        {
            // Nenhum produto passou do corte. Gravar so a aritmetica e correto e
            // barato: a conta fica com o diagnostico registrado, o Orchestrator
            // a manda para nurture pelo tier, e nao se gastou modelo para
            // escrever cinco argumentos que ninguem leria.
            await persister.PersistAsync(new ProductFitPersistRequest
            {
                AccountId = payload.AccountId,
                ResearchRunId = payload.ResearchRunId,
                OutboxEventId = evt.Id,
                AccountScoreId = facts.AccountScoreId,
                Fits = calculated,
                Pitch = null,
                AgentRun = BuildAgentRun(
                    payload, startedAt,
                    new AgentRunResult { RawText = string.Empty, Succeeded = true },
                    RunStatus.Completed, null)
            }, ct);

            logger.LogInformation(
                "Conta {AccountId}: nenhum produto acima do corte; fit gravado sem argumento.",
                payload.AccountId);

            return;
        }

        var systemPrompt = prompts.BuildSystemPrompt();

        var result = await runtime.RunAsync(new AgentRunRequest
        {
            AgentName = prompts.AgentName,
            PromptVersion = prompts.PromptVersion,
            SystemPrompt = systemPrompt,
            UserPrompt = prompts.BuildUserPrompt(context, worthPitching),
            SessionId = payload.ResearchRunId.ToString(),
            FixtureScenario = payload.FixtureScenario,
            Metadata = new Dictionary<string, string>
            {
                ["account_id"] = payload.AccountId.ToString(),
                ["research_run_id"] = payload.ResearchRunId.ToString(),
                ["entry_product"] = entry?.Product ?? string.Empty
            }
        }, ct);

        if (!result.Succeeded)
        {
            await FailAsync(payload, startedAt, result, "agent_runtime_error",
                [new SchemaViolation(string.Empty, result.Error ?? "Falha desconhecida no runtime.")], ct);

            throw new AgentRuntimeException(result.Error ?? "Runtime de agente falhou.");
        }

        var outcome = validator.Validate<ProductPitchProfile>(result.RawText);
        var violations = outcome.IsValid
            ? EvidenceFirstGuard.Check(outcome.Value!)
            : outcome.Violations;

        // 3. Uma tentativa de reparo, pela mesma razao dos outros dois: se o
        //    modelo nao acerta com os erros em maos, o problema e do prompt.
        if (violations.Count > 0)
        {
            logger.LogWarning(
                "Output do Product Matcher rejeitado para a conta {AccountId}; tentando reparo. Violacoes: {Count}",
                payload.AccountId, violations.Count);

            var describe = string.Join("\n", violations.Select(v => $"- {v}"));

            var repaired = await runtime.RunAsync(new AgentRunRequest
            {
                AgentName = prompts.AgentName,
                PromptVersion = prompts.PromptVersion,
                SystemPrompt = systemPrompt,
                UserPrompt = prompts.BuildRepairPrompt(context, worthPitching, result.RawText, describe),
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

                outcome = validator.Validate<ProductPitchProfile>(result.RawText);
                violations = outcome.IsValid ? EvidenceFirstGuard.Check(outcome.Value!) : outcome.Violations;
            }
        }

        if (violations.Count > 0)
        {
            await FailAsync(payload, startedAt, result, "contract_violation", violations, ct);

            throw new InvalidOperationException(
                $"Argumento de produto rejeitado apos reparo: {violations.Count} violacao(oes).");
        }

        // 4. Argumento validado, mas so vale para produto que a plataforma
        //    pediu. Um pitch de produto nao solicitado nao tem nota calculada, e
        //    uma linha de product_fit sem score nao entra na fila - entao ele e
        //    descartado aqui em vez de virar uma linha orfa no banco.
        var requested = worthPitching.Select(f => f.Product).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = outcome.Value! with
        {
            Pitches = [.. outcome.Value.Pitches.Where(p => requested.Contains(p.Product))]
        };

        var dropped = outcome.Value.Pitches.Count - accepted.Pitches.Count;

        if (dropped > 0)
        {
            logger.LogWarning(
                "Conta {AccountId}: {Dropped} pitch(es) de produto nao solicitado descartado(s).",
                payload.AccountId, dropped);
        }

        await persister.PersistAsync(new ProductFitPersistRequest
        {
            AccountId = payload.AccountId,
            ResearchRunId = payload.ResearchRunId,
            OutboxEventId = evt.Id,
            AccountScoreId = facts.AccountScoreId,
            Fits = calculated,
            Pitch = accepted,
            AgentRun = BuildAgentRun(payload, startedAt, result, RunStatus.Completed, null)
        }, ct);

        logger.LogInformation(
            "Fit gravado para a conta {AccountId}: entrada {Entry}, {Pitches} argumento(s), " +
            "{Disqualifiers} desqualificador(es)",
            payload.AccountId, entry?.Product ?? "(nenhuma)",
            accepted.Pitches.Count, accepted.Disqualifiers.Count);
    }

    /// <summary>
    /// Produtos que valem uma passada do modelo: o de entrada, mais os que
    /// pontuaram perto dele.
    ///
    /// O corte relativo, e nao absoluto, existe porque "perto do primeiro" e a
    /// pergunta certa. Num grupo com dor em tudo, o segundo produto pontua 71 e
    /// merece argumento; numa revenda com um problema so, o segundo pontua 22 e
    /// escrever a defesa dele produziria um texto que o SDR nunca usaria - e que
    /// pareceria autoritativo o suficiente para alguem usar mesmo assim.
    /// </summary>
    private static IReadOnlyList<ProductFit> Worthwhile(IReadOnlyList<ProductFit> calculated)
    {
        var top = calculated.Max(f => f.Score);

        if (top < ProductFitScoring.EntryThreshold) return [];

        return [.. calculated
            .Where(f => f.Score >= Math.Max(ProductFitScoring.EntryThreshold, top * 0.7m))
            .OrderByDescending(f => f.RecommendedEntry)
            .ThenByDescending(f => f.Score)
            .Take(3)];
    }

    private static ProductFitInputs ToInputs(ProductFitFacts facts, DateTimeOffset now) => new()
    {
        ReferenceDate = now,
        Operation = CnaeCatalog.FromSegment(facts.Segment),
        StoreCount = facts.StoreCount,
        InventoryEstimate = facts.InventoryEstimate,
        CnpjCount = Math.Max(facts.CnpjCount, 1),
        BrandCount = facts.BrandCount,
        HasAuthorizedBrand = facts.HasAuthorizedBrand,
        Audit = facts.Audit,
        Technologies = facts.Technologies,
        Signals = facts.Signals
    };

    /// <summary>
    /// Registra a falha fora de qualquer transacao de negocio: o custo do agente
    /// ja foi incorrido e o motivo precisa sobreviver ao rollback.
    ///
    /// Como a auditoria e diferente do Researcher, aqui NAO ha transicao de
    /// estado a desfazer: casar produto nao move a conta na maquina de estados.
    /// </summary>
    private async Task FailAsync(
        MatchRequestedPayload payload,
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
            "Product Matcher falhou para a conta {AccountId} ({Reason}): {Violations}",
            payload.AccountId, reason, error);
    }

    private AgentRun BuildAgentRun(
        MatchRequestedPayload payload,
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

public sealed record MatchRequestedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("account_id")]
    public Guid AccountId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("research_run_id")]
    public Guid ResearchRunId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("fixture_scenario")]
    public string? FixtureScenario { get; init; }
}
