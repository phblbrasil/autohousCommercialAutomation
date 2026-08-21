using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>Fatos crus da conta, do jeito que o banco os tem, antes de virar score.</summary>
public sealed record AccountScoringFacts
{
    public required Guid AccountId { get; init; }
    public string? Segment { get; init; }
    public int? StoreCount { get; init; }
    public int? InventoryEstimate { get; init; }
    public int CnpjCount { get; init; }
    public int BrandCount { get; init; }
    public bool HasAuthorizedBrand { get; init; }
    public IReadOnlyList<ScoredSignal> Signals { get; init; } = [];
    public WebsiteAuditFacts? Audit { get; init; }
    public ContactabilityFacts Contacts { get; init; } = new();
}

public sealed record AccountScoreView
{
    public required Guid AccountId { get; init; }
    public required decimal TotalScore { get; init; }
    public required decimal CompanyFit { get; init; }
    public required decimal TechnologyPain { get; init; }
    public required decimal BuyingSignal { get; init; }
    public required decimal Contactability { get; init; }
    public required string ScoringVersion { get; init; }
    public required string FeatureSnapshotJson { get; init; }
    public required DateTimeOffset CalculatedAt { get; init; }
}

public interface IAccountScoreRepository
{
    /// <summary>Reune os fatos das varias tabelas em uma leitura so.</summary>
    Task<AccountScoringFacts?> LoadFactsAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Insere uma nova linha em <c>account_scores</c>. A tabela e append-only: o
    /// historico e o que permite responder "por que esta conta caiu de 82 para
    /// 68?". A view <c>v_account_current_score</c> aponta para o vigente.
    /// </summary>
    Task InsertAsync(IUnitOfWork uow, Guid scoreId, Guid accountId, OpportunityScore score, string featureSnapshotJson, CancellationToken ct = default);

    Task UpdateAccountTierAsync(IUnitOfWork uow, Guid accountId, short tier, CancellationToken ct = default);

    Task<AccountScoreView?> GetCurrentAsync(Guid accountId, CancellationToken ct = default);
}
