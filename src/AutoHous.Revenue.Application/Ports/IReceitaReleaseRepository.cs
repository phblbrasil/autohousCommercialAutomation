namespace AutoHous.Revenue.Application;

/// <summary>Um arquivo baixado, com o que prova que ele e o mesmo da origem.</summary>
public sealed record ReceitaFileDigest(string Name, long Length, string Sha256);

public sealed record ReceitaReleaseSummary
{
    public required Guid Id { get; init; }
    public required string Release { get; init; }
    public string? SourceUri { get; init; }
    public required string Status { get; init; }
    public long EstablishmentsScanned { get; init; }
    public long EstablishmentsSelected { get; init; }
    public long CompaniesJoined { get; init; }
    public long PartnersLoaded { get; init; }
    public Guid? BatchId { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>Status possiveis de <c>receita_releases.status</c>.</summary>
public static class ReceitaReleaseStatus
{
    public const string Downloading = "downloading";
    public const string Downloaded = "downloaded";
    public const string Streamed = "streamed";
    public const string Loaded = "loaded";
    public const string Failed = "failed";
}

/// <summary>
/// Lineage da fonte: que competencia da Receita gerou que carga, e com que
/// arquivos.
///
/// Existe porque o filtro de CNAE acontece no stream - <c>companies_raw</c> nao
/// guarda tudo o que foi lido. O SHA-256 de cada zip e o que sustenta a promessa
/// de que reimportar o mesmo release da o mesmo resultado.
/// </summary>
public interface IReceitaReleaseRepository
{
    Task<Guid> StartAsync(IUnitOfWork uow, Guid id, string release, string? sourceUri, CancellationToken ct = default);

    Task RecordFilesAsync(IUnitOfWork uow, string release, IReadOnlyList<ReceitaFileDigest> files, CancellationToken ct = default);

    Task RecordProgressAsync(
        IUnitOfWork uow,
        string release,
        string status,
        long establishmentsScanned,
        long establishmentsSelected,
        long companiesJoined,
        long partnersLoaded,
        Guid? batchId,
        CancellationToken ct = default);

    Task FinishAsync(IUnitOfWork uow, string release, string status, string? notes, CancellationToken ct = default);

    Task<ReceitaReleaseSummary?> GetAsync(string release, CancellationToken ct = default);

    Task<IReadOnlyList<ReceitaReleaseSummary>> ListAsync(int limit, CancellationToken ct = default);
}
