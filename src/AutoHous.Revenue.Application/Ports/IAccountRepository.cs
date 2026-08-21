using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

public interface IAccountRepository
{
    Task<Account?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Account?> GetByCnpjAsync(string cnpj, CancellationToken ct = default);
    Task<Guid> CreateFromCnpjAsync(string cnpj, string name, string? razaoSocial, string? uf, string? municipio, CancellationToken ct = default);
    Task TransitionAsync(IUnitOfWork uow, Guid accountId, AccountStatus from, AccountStatus to, CancellationToken ct = default);
    Task<AccountContext?> GetContextAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Projecao unica de contexto: alimenta tanto o prompt do agente quanto a
/// ferramenta MCP get_account_context. Manter uma so evita que o agente enxergue
/// um contexto diferente do que a plataforma acredita ter enviado.
/// </summary>
public sealed record AccountContext
{
    public required Guid AccountId { get; init; }
    public required string Name { get; init; }
    public string? Domain { get; init; }
    public string? Segment { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public required string Status { get; init; }
    public int? StoreCount { get; init; }
    public DateTimeOffset? LastResearchedAt { get; init; }
    public IReadOnlyList<string> Cnpjs { get; init; } = [];
    public IReadOnlyList<string> KnownBrands { get; init; } = [];
    public int EvidenceCount { get; init; }
}
