using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

public sealed record MergeCandidateView
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string AccountName { get; init; }

    /// <summary>Linha crua que originou o candidato; renormalizada na decisao.</summary>
    public Guid? RawId { get; init; }
    public string? AccountUf { get; init; }
    public required string IncomingCnpj { get; init; }
    public required string IncomingName { get; init; }
    public string? IncomingUf { get; init; }
    public string? IncomingMunicipio { get; init; }
    public required decimal Similarity { get; init; }
    public required string Reason { get; init; }
    public required string Status { get; init; }

    /// <summary>Faixa do §11 correspondente a similaridade — auto | provavel | revisao.</summary>
    public string Band => AccountSimilarity.Classify(Similarity);
}

public sealed record MergeCandidateRecord
{
    public required Guid AccountId { get; init; }
    public required Guid RawId { get; init; }
    public required string IncomingCnpj { get; init; }
    public required string IncomingName { get; init; }
    public string? IncomingUf { get; init; }
    public string? IncomingMunicipio { get; init; }
    public required decimal Similarity { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Persistencia do account graph: busca de candidatos, criacao/vinculo de conta
/// e fila de revisao.
///
/// A busca por similaridade e responsabilidade do adaptador porque ela e
/// executada pelo Postgres (pg_trgm, indice GIN) sobre a base inteira - trazer
/// milhares de contas para a memoria e comparar em C# seria a mesma decisao
/// tomada no lugar errado.
/// </summary>
public interface IAccountGraphRepository
{
    Task<IReadOnlyList<AccountGroupCandidate>> FindCandidatesAsync(
        string cnpjRoot, string normalizedName, decimal minimumSimilarity, int limit, CancellationToken ct = default);

    /// <summary>Cria a conta e vincula o CNPJ, na transacao do chamador.</summary>
    Task<Guid> CreateAccountForCompanyAsync(IUnitOfWork uow, Guid accountId, NormalizedCompany company, decimal graphConfidence, CancellationToken ct = default);

    /// <summary>Liga mais um CNPJ a uma conta existente.</summary>
    Task AttachCompanyAsync(IUnitOfWork uow, Guid accountId, NormalizedCompany company, CancellationToken ct = default);

    Task<Guid?> FindAccountByCnpjAsync(string cnpj, CancellationToken ct = default);

    Task RecordMergeCandidateAsync(IUnitOfWork uow, Guid candidateId, MergeCandidateRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<MergeCandidateView>> ListPendingCandidatesAsync(int limit, CancellationToken ct = default);

    Task<MergeCandidateView?> GetCandidateAsync(Guid candidateId, CancellationToken ct = default);

    Task DecideCandidateAsync(IUnitOfWork uow, Guid candidateId, bool approved, string? decidedBy, CancellationToken ct = default);
}
