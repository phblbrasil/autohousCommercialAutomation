using System.Text.Json;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public enum ScoreAccountOutcome
{
    Scored,
    AccountNotFound,

    /// <summary>Conta suprimida nao entra em fila de execucao — nao ha o que priorizar.</summary>
    AccountSuppressed
}

public sealed record ScoreAccountResult(
    ScoreAccountOutcome Outcome,
    OpportunityScore? Score = null);

/// <summary>
/// Consumidor de <c>research.completed</c>: recalcula o Opportunity Score e
/// promove a conta de <c>researched</c> para <c>scored</c>.
///
/// Ate esta entrega, <c>research.completed</c> era marcado como processado sem
/// consumidor — a cadeia de eventos do frame 02 da V2 parava na pesquisa.
///
/// Nenhum agente e chamado aqui. O score e aritmetica sobre fatos ja
/// persistidos: roda em milissegundos, custa zero e pode ser reexecutado a
/// vontade quando um sinal novo chega.
/// </summary>
public sealed class ScoreAccountUseCase(
    IAccountRepository accounts,
    IAccountScoreRepository scores,
    IOutboxRepository outbox,
    IUnitOfWorkFactory unitOfWork,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<ScoreAccountUseCase> logger)
{
    public async Task<ScoreAccountResult> ExecuteAsync(
        Guid accountId, Guid? sourceEventId = null, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct);

        if (account is null) return new ScoreAccountResult(ScoreAccountOutcome.AccountNotFound);

        if (account.Status == AccountStatus.Suppressed)
        {
            return new ScoreAccountResult(ScoreAccountOutcome.AccountSuppressed);
        }

        var facts = await scores.LoadFactsAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Fatos de scoring indisponiveis para {accountId}.");

        var inputs = ToInputs(facts, clock.UtcNow);
        var score = OpportunityScoring.Calculate(inputs);

        var snapshot = JsonSerializer.Serialize(new
        {
            inputs = new
            {
                facts.Segment,
                facts.StoreCount,
                facts.InventoryEstimate,
                facts.CnpjCount,
                facts.BrandCount,
                facts.HasAuthorizedBrand,
                signals = facts.Signals.Count,
                audited = facts.Audit is not null
            },
            breakdown = score.Breakdown,
            score.Band,
            score.Tier,
            score.Coverage
        }, SnapshotJson);

        await using var uow = await unitOfWork.BeginAsync(ct);

        await scores.InsertAsync(uow, ids.NewId(), accountId, score, snapshot, ct);
        await scores.UpdateAccountTierAsync(uow, accountId, score.Tier, ct);

        // researched -> scored. Um recalculo de conta que ja passou de scored
        // (contactada, engajada) nao pode empurra-la para tras no funil.
        if (AccountStatusTransitions.CanTransition(account.Status, AccountStatus.Scored) &&
            account.Status == AccountStatus.Researched)
        {
            await accounts.TransitionAsync(uow, accountId, account.Status, AccountStatus.Scored, ct);
        }

        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = ids.NewId(),
            EventType = EventTypes.ScoreReady,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                total_score = score.Total,
                tier = score.Tier,
                band = score.Band,
                coverage = score.Coverage
            }),
            IdempotencyKey = IdempotencyKey.ForScore(accountId, clock.UtcNow),
            Status = OutboxStatus.Pending,
            AvailableAt = clock.UtcNow
        }, ct);

        if (sourceEventId is { } eventId)
        {
            await outbox.MarkProcessedAsync(uow, eventId, ct);
        }

        await uow.CommitAsync(ct);

        logger.LogInformation(
            "Conta {AccountId} pontuada: {Total} ({Band}, tier {Tier}), cobertura {Coverage:P0}",
            accountId, score.Total, score.Band, score.Tier, score.Coverage);

        return new ScoreAccountResult(ScoreAccountOutcome.Scored, score);
    }

    /// <summary>
    /// O snapshot vai para uma coluna <c>jsonb</c> consultada com SQL. Manter
    /// snake_case alinha as chaves com o resto do schema: quem escreve
    /// <c>feature_snapshot -&gt; 'company_fit'</c> nao precisa lembrar que ali,
    /// e so ali, a convencao muda.
    /// </summary>
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static ScoringInputs ToInputs(AccountScoringFacts facts, DateTimeOffset now) => new()
    {
        ReferenceDate = now,
        Operation = ParseOperation(facts.Segment),
        StoreCount = facts.StoreCount,
        InventoryEstimate = facts.InventoryEstimate,
        CnpjCount = Math.Max(facts.CnpjCount, 1),
        BrandCount = facts.BrandCount,
        HasAuthorizedBrand = facts.HasAuthorizedBrand,
        Signals = facts.Signals,
        Audit = facts.Audit,
        Contacts = facts.Contacts
    };

    /// <summary>
    /// <c>accounts.segment</c> e texto livre: o pipeline de ingestao grava o
    /// rotulo derivado do CNAE, mas o Researcher tambem pode sobrescrever com o
    /// que encontrou no site. Nao reconhecer o valor deixa a dimensao como nao
    /// observada em vez de chutar.
    /// </summary>
    private static AutomotiveOperation? ParseOperation(string? segment) =>
        segment?.Trim().ToLowerInvariant() switch
        {
            "concessionaria" => AutomotiveOperation.Concessionaria,
            "revenda" => AutomotiveOperation.Revenda,
            "atacado" => AutomotiveOperation.Atacado,
            "intermediacao" => AutomotiveOperation.Intermediacao,
            "oficina" => AutomotiveOperation.Oficina,
            "autopecas" => AutomotiveOperation.Autopecas,
            "motos" => AutomotiveOperation.Motos,
            "locadora" => AutomotiveOperation.Locadora,
            _ => null
        };
}

public sealed record ResearchCompletedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("account_id")]
    public Guid AccountId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("research_run_id")]
    public Guid ResearchRunId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("completeness")]
    public decimal Completeness { get; init; }
}
