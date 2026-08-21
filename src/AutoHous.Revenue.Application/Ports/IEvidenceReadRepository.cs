namespace AutoHous.Revenue.Application;

/// <summary>
/// Leitura de evidencias de uma conta.
///
/// Existe porque o endpoint <c>GET /accounts/{id}/evidence</c> montava e executava
/// SQL com Dapper dentro do proprio lambda. §11 e §24 da skill: SQL vive na
/// infraestrutura e controller nao acessa banco diretamente.
/// </summary>
public interface IEvidenceReadRepository
{
    Task<IReadOnlyList<EvidenceListItem>> ListForAccountAsync(Guid accountId, CancellationToken ct = default);
}

public sealed record EvidenceListItem
{
    public required Guid Id { get; init; }
    public required string ClaimType { get; init; }
    public required string ClaimText { get; init; }
    public decimal? Confidence { get; init; }
    public string? SourceUrl { get; init; }
    public string? SourceTitle { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}
