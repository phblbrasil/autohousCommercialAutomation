using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Fatos que alimentam <see cref="ProductFitScoring"/>, do jeito que o banco os
/// tem.
///
/// Sobrepoe <see cref="AccountScoringFacts"/> em varios campos, e nao herda dele
/// de proposito: o Opportunity Score pergunta "quanta dor?" e este pergunta
/// "dor de que?". Unificar os dois obrigaria uma leitura a carregar as sete
/// notas de auditoria e a pilha de tecnologias para calcular quatro dimensoes
/// que nao usam nenhuma das duas.
/// </summary>
public sealed record ProductFitFacts
{
    public required Guid AccountId { get; init; }

    /// <summary>
    /// Safra de score a que estes fatos pertencem. E a ancora de idempotencia do
    /// Product Matcher: fit novo so faz sentido sobre score novo.
    /// </summary>
    public Guid? AccountScoreId { get; init; }

    public string? Segment { get; init; }
    public int? StoreCount { get; init; }
    public int? InventoryEstimate { get; init; }
    public int CnpjCount { get; init; }
    public int BrandCount { get; init; }
    public bool HasAuthorizedBrand { get; init; }

    public WebsiteAuditDetail? Audit { get; init; }
    public IReadOnlyList<AccountTechnology> Technologies { get; init; } = [];
    public IReadOnlyList<ScoredSignal> Signals { get; init; } = [];
}

/// <summary>Uma safra de <c>product_fit</c> ja gravada.</summary>
public sealed record ProductFitView
{
    public required Guid AccountId { get; init; }
    public required string Product { get; init; }
    public required decimal Score { get; init; }
    public required bool RecommendedEntry { get; init; }
    public required string ReasonsJson { get; init; }
    public string? ObjectionsJson { get; init; }
    public string? RecommendedPersonasJson { get; init; }
    public required DateTimeOffset CalculatedAt { get; init; }
}

public interface IProductFitRepository
{
    Task<ProductFitFacts?> LoadFactsAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Safra vigente, pela view <c>v_account_current_fit</c>.</summary>
    Task<IReadOnlyList<ProductFitView>> GetCurrentAsync(Guid accountId, CancellationToken ct = default);
}

/// <summary>
/// Grava uma safra de fit validada em UMA transacao: sources, evidence, as
/// linhas de <c>product_fit</c> com a ligacao para as evidencias, os
/// desqualificadores como sinais negativos, o research_run, o agent_run, o
/// evento de saida e a baixa do evento de entrada.
///
/// Mesma garantia dos outros dois persisters, e pela mesma razao: nao pode
/// existir estado em que a conta tem um argumento comercial gravado sem as
/// evidencias que o sustentam - e este e o argumento que chega ao SDR.
/// </summary>
public interface IProductFitPersister
{
    Task PersistAsync(ProductFitPersistRequest request, CancellationToken ct = default);
}

public sealed record ProductFitPersistRequest
{
    public required Guid AccountId { get; init; }
    public required Guid ResearchRunId { get; init; }
    public required Guid OutboxEventId { get; init; }

    /// <summary>Safra de score que originou este fit. Vai para a chave de idempotencia.</summary>
    public Guid? AccountScoreId { get; init; }

    /// <summary>A aritmetica. Uma linha de <c>product_fit</c> por item.</summary>
    public required IReadOnlyList<ProductFit> Fits { get; init; }

    /// <summary>
    /// A narrativa. Nula quando o agente falhou de um jeito que nao justifica
    /// perder a aritmetica - o fit calculado ja prioriza a fila, e um argumento
    /// pode ser escrito depois.
    /// </summary>
    public ProductPitchProfile? Pitch { get; init; }

    public required AgentRun AgentRun { get; init; }
}
