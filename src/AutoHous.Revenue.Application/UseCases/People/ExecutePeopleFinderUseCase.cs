using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

/// <summary>
/// People Finder (A05): monta o briefing, pede os contatos, valida e grava.
///
/// Repete a forma dos outros tres - agente, validacao, uma tentativa de reparo,
/// persistencia transacional - com uma diferenca que nao e de forma e sim de
/// consequencia: o que sai daqui e PII de pessoa fisica.
///
/// Isso muda duas decisoes:
///
/// 1. **Falha nao e recuperavel parcialmente.** No Product Matcher, agente que
///    falha ainda deixa a aritmetica gravada. Aqui nao existe metade util: sem
///    contato validado, nao ha nada a escrever, e gravar um contato de confianca
///    duvidosa "para nao perder o run" e exatamente o que a politica proibe.
/// 2. **A busca vazia e um sucesso.** Um run que devolve zero contatos com
///    <c>searched_without_result</c> preenchido completa normalmente e marca a
///    conta como pesquisada. Sem isso o Orchestrator pediria a mesma busca
///    para sempre - a distincao entre "nao temos" e "ja procuramos" e
///    justamente o que <c>ContactsSearchedAt</c> existe para guardar.
///
/// Quem monta o briefing e a plataforma, a partir do fit ja calculado: as
/// personas a procurar saem do produto de entrada. Deixar o agente escolher
/// quem procurar produziria o organograma inteiro, e vinte pessoas custam vinte
/// evidencias e vinte linhas de PII para aproveitar duas.
/// </summary>
public sealed class ExecutePeopleFinderUseCase(
    IAccountRepository accounts,
    IProductFitRepository fits,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IContactPersister persister,
    IAgentRuntime runtime,
    IStructuredOutputValidator validator,
    IPeopleFinderPromptBuilder prompts,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<ExecutePeopleFinderUseCase> logger)
{
    public async Task ExecuteAsync(OutboxEvent evt, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Deserialize<ContactsRequestedPayload>(evt.PayloadJson)
            ?? throw new InvalidOperationException($"Payload invalido no evento {evt.Id}.");

        var context = await accounts.GetContextAsync(payload.AccountId, ct)
            ?? throw new InvalidOperationException($"Contexto da conta {payload.AccountId} indisponivel.");

        var brief = await BuildBriefAsync(payload.AccountId, ct);

        if (brief.Personas.Count == 0)
        {
            // Sem personas nao ha busca a fazer. Falha de PRE-CONDICAO, como o
            // dominio ausente na auditoria: quem define as personas e o Product
            // Matcher, e mandar o agente procurar "quem decide" em abstrato
            // devolveria o organograma inteiro.
            await FailAsync(payload, clock.UtcNow, AgentRunResult.Failure("Conta sem personas."),
                "missing_personas",
                [new SchemaViolation("/personas", "Conta sem fit de produto; rode o Product Matcher antes.")],
                ct);

            throw new InvalidOperationException(
                $"Conta {payload.AccountId} nao tem personas definidas para buscar.");
        }

        var startedAt = clock.UtcNow;
        var systemPrompt = prompts.BuildSystemPrompt();

        var result = await runtime.RunAsync(new AgentRunRequest
        {
            AgentName = prompts.AgentName,
            PromptVersion = prompts.PromptVersion,
            SystemPrompt = systemPrompt,
            UserPrompt = prompts.BuildUserPrompt(context, brief),
            SessionId = payload.ResearchRunId.ToString(),
            FixtureScenario = payload.FixtureScenario,
            Metadata = new Dictionary<string, string>
            {
                ["account_id"] = payload.AccountId.ToString(),
                ["research_run_id"] = payload.ResearchRunId.ToString(),
                ["entry_product"] = brief.EntryProduct ?? string.Empty
            }
        }, ct);

        if (!result.Succeeded)
        {
            await FailAsync(payload, startedAt, result, "agent_runtime_error",
                [new SchemaViolation(string.Empty, result.Error ?? "Falha desconhecida no runtime.")], ct);

            throw new AgentRuntimeException(result.Error ?? "Runtime de agente falhou.");
        }

        var outcome = validator.Validate<ContactDiscoveryProfile>(result.RawText);
        var violations = outcome.IsValid
            ? EvidenceFirstGuard.Check(outcome.Value!)
            : outcome.Violations;

        // Uma tentativa de reparo. Vale mais aqui do que nos outros agentes: as
        // violacoes mais comuns deste contrato - canal reusando a evidencia do
        // contato, confianca abaixo do piso - sao mecanicas e o modelo corrige
        // com os erros em maos, sem precisar buscar de novo.
        if (violations.Count > 0)
        {
            logger.LogWarning(
                "Output do People Finder rejeitado para a conta {AccountId}; tentando reparo. Violacoes: {Count}",
                payload.AccountId, violations.Count);

            var describe = string.Join("\n", violations.Select(v => $"- {v}"));

            var repaired = await runtime.RunAsync(new AgentRunRequest
            {
                AgentName = prompts.AgentName,
                PromptVersion = prompts.PromptVersion,
                SystemPrompt = systemPrompt,
                UserPrompt = prompts.BuildRepairPrompt(context, brief, result.RawText, describe),
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

                outcome = validator.Validate<ContactDiscoveryProfile>(result.RawText);
                violations = outcome.IsValid ? EvidenceFirstGuard.Check(outcome.Value!) : outcome.Violations;
            }
        }

        if (violations.Count > 0)
        {
            await FailAsync(payload, startedAt, result, "contract_violation", violations, ct);

            throw new InvalidOperationException(
                $"Descoberta de contatos rejeitada apos reparo: {violations.Count} violacao(oes).");
        }

        var persisted = await persister.PersistAsync(new ContactPersistRequest
        {
            AccountId = payload.AccountId,
            ResearchRunId = payload.ResearchRunId,
            OutboxEventId = evt.Id,
            Profile = outcome.Value!,
            AccountDomain = context.Domain,
            AgentRun = BuildAgentRun(payload, startedAt, result, RunStatus.Completed, null)
        }, ct);

        logger.LogInformation(
            "Contatos gravados para a conta {AccountId}: {Contacts} pessoa(s), {Channels} canal(is), " +
            "decisor={HasDecisionMaker}. Nao encontrados: {NotFound}",
            payload.AccountId, persisted.ContactsPersisted, persisted.ChannelsPersisted,
            persisted.HasDecisionMaker,
            outcome.Value!.SearchedWithoutResult.Count == 0
                ? "(nenhum registrado)"
                : string.Join(", ", outcome.Value.SearchedWithoutResult));

        if (persisted.RejectedByPolicy.Count > 0)
        {
            logger.LogWarning(
                "Conta {AccountId}: {Count} item(ns) recusado(s) pela politica de PII: {Reasons}",
                payload.AccountId, persisted.RejectedByPolicy.Count,
                string.Join("; ", persisted.RejectedByPolicy));
        }
    }

    /// <summary>
    /// Monta o briefing a partir da safra de fit vigente.
    ///
    /// As personas saem do produto de ENTRADA, e nao da uniao de todos os
    /// produtos. Um grupo pode ter fit alto em MotorHub e BoxTech, e as personas
    /// dos dois somam nove cargos - buscar os nove custa nove buscas para
    /// sustentar uma conversa so, que e a do produto de entrada.
    ///
    /// Quando o Product Matcher restringiu as personas, a lista dele vence: ele
    /// olhou a operacao e concluiu que ali nao existe diretor de marketing.
    /// </summary>
    private async Task<PeopleSearchBrief> BuildBriefAsync(Guid accountId, CancellationToken ct)
    {
        var current = await fits.GetCurrentAsync(accountId, ct);

        var entry = current.FirstOrDefault(f => f.RecommendedEntry)
                 ?? current.OrderByDescending(f => f.Score).FirstOrDefault();

        if (entry is null) return new PeopleSearchBrief { Personas = [] };

        var restricted = Deserialize(entry.RecommendedPersonasJson);

        var personas = restricted.Count > 0
            ? restricted
            : ProductCatalog.Find(entry.Product)?.Personas ?? [];

        return new PeopleSearchBrief
        {
            EntryProduct = entry.Product,
            Personas = personas,
            Angle = Angle(entry.ReasonsJson)
        };
    }

    private static IReadOnlyList<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // Coluna jsonb escrita por uma versao anterior do persister. Cair
            // para o catalogo e melhor que derrubar a busca inteira.
            return [];
        }
    }

    /// <summary>
    /// O <c>angle</c> de <c>product_fit.reasons</c>, quando o Product Matcher
    /// chegou a escrever um.
    ///
    /// A coluna guarda um objeto com <c>angle</c>, <c>criteria</c> (a aritmetica)
    /// e <c>narrative</c> (o argumento do agente) - e nao um array - justamente
    /// porque as duas metades tem naturezas diferentes e precisam ser lidas
    /// separadamente. Ausente quando o agente falhou e so a aritmetica foi
    /// gravada, que e um estado previsto.
    ///
    /// O People Finder nao reescreve o angulo; ele so precisa saber que uma
    /// conversa sobre integracao de estoque pede alguem de operacoes, e nao o
    /// gerente da loja mais proxima.
    /// </summary>
    private static string? Angle(string? reasonsJson)
    {
        if (string.IsNullOrWhiteSpace(reasonsJson)) return null;

        try
        {
            using var document = JsonDocument.Parse(reasonsJson);

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("angle", out var angle) &&
                   angle.ValueKind == JsonValueKind.String
                ? angle.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task FailAsync(
        ContactsRequestedPayload payload,
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
            "People Finder falhou para a conta {AccountId} ({Reason}): {Violations}",
            payload.AccountId, reason, error);
    }

    private AgentRun BuildAgentRun(
        ContactsRequestedPayload payload,
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

public sealed record ContactsRequestedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("account_id")]
    public Guid AccountId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("research_run_id")]
    public Guid ResearchRunId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("fixture_scenario")]
    public string? FixtureScenario { get; init; }
}
