namespace AutoHous.Revenue.Application;

/// <summary>
/// Porta de consulta (§26: nem toda leitura precisa reconstruir agregados).
/// Devolve projecoes prontas para apresentacao; como elas sao produzidas -
/// tsvector, trigrama, indice GIN - e detalhe do adaptador.
/// </summary>
public interface ISearchRepository
{
    Task<IReadOnlyList<AccountSearchHit>> SearchAccountsAsync(string query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<EvidenceSearchHit>> SearchEvidenceAsync(string query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<SimilarAccountHit>> FindSimilarAccountsAsync(Guid accountId, decimal threshold, int limit, CancellationToken ct = default);
}

public sealed record AccountSearchHit
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Domain { get; init; }
    public string? Segment { get; init; }
    public string? City { get; init; }
    public required string Status { get; init; }
    public required double Rank { get; init; }
}

public sealed record EvidenceSearchHit
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string AccountName { get; init; }
    public required string ClaimType { get; init; }
    public required string ClaimText { get; init; }
    /// <summary>Trecho com os termos casados destacados por [ ].</summary>
    public required string Headline { get; init; }
    public decimal? Confidence { get; init; }
    public string? SourceUrl { get; init; }
    public required double Rank { get; init; }
}

public sealed record SimilarAccountHit
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? NormalizedName { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    /// <summary>Similaridade trigrama, 0 a 1.</summary>
    public required decimal Similarity { get; init; }
    /// <summary>Faixa de decisao do §11: auto | provavel | revisao.</summary>
    public required string Recommendation { get; init; }
}
