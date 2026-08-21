using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application.Tests;

/// <summary>
/// Portas falsas da camada 01. Permitem exercitar a carga da Receita inteira -
/// quatro passadas, filtro de origem, agregado e juncao - sem baixar 7 GB e sem
/// Postgres.
/// </summary>
public sealed class FakeReceitaSourceReader : IReceitaSourceReader
{
    public List<ReceitaEstabelecimento> Estabelecimentos { get; } = [];
    public List<ReceitaEmpresa> Empresas { get; } = [];
    public List<ReceitaSimples> Simples { get; } = [];
    public List<ReceitaSocio> Socios { get; } = [];
    public ReceitaDomainTables Tables { get; set; } = new();

    public List<ReceitaFileSet> Requested { get; } = [];

    /// <summary>Quantas vezes cada arquivo foi varrido. Uma passada extra sobre 63M linhas nao e detalhe.</summary>
    public Dictionary<string, int> Scans { get; } = [];

    public Task<IReadOnlyList<ReceitaFileDigest>> EnsureLocalAsync(
        string release, ReceitaFileSet files, CancellationToken ct = default)
    {
        Requested.Add(files);

        return Task.FromResult<IReadOnlyList<ReceitaFileDigest>>(
            [new ReceitaFileDigest("Estabelecimentos0.zip", 2_098, "ABC")]);
    }

    public Task<ReceitaDomainTables> ReadDomainTablesAsync(string release, CancellationToken ct = default) =>
        Task.FromResult(Tables);

    public IAsyncEnumerable<ReceitaEstabelecimento> ReadEstabelecimentosAsync(
        string release, CancellationToken ct = default) => Stream(Estabelecimentos, "estabelecimentos");

    public IAsyncEnumerable<ReceitaEmpresa> ReadEmpresasAsync(
        string release, CancellationToken ct = default) => Stream(Empresas, "empresas");

    public IAsyncEnumerable<ReceitaSimples> ReadSimplesAsync(
        string release, CancellationToken ct = default) => Stream(Simples, "simples");

    public IAsyncEnumerable<ReceitaSocio> ReadSociosAsync(
        string release, CancellationToken ct = default) => Stream(Socios, "socios");

    private async IAsyncEnumerable<T> Stream<T>(IEnumerable<T> items, string name)
    {
        Scans[name] = Scans.GetValueOrDefault(name) + 1;

        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }
}

public sealed class FakeReceitaSpool : IReceitaSpool
{
    public Dictionary<string, List<RawCompanyRow>> Files { get; } = [];
    public List<string> Deleted { get; } = [];

    public Task ResetAsync(string name, CancellationToken ct = default)
    {
        Files[name] = [];
        return Task.CompletedTask;
    }

    public Task AppendAsync(
        string name, IReadOnlyList<RawCompanyRow> rows, CancellationToken ct = default)
    {
        if (!Files.TryGetValue(name, out var target)) Files[name] = target = [];
        target.AddRange(rows);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RawCompanyRow> ReadAsync(string name, CancellationToken ct = default)
    {
        foreach (var row in Files.GetValueOrDefault(name) ?? [])
        {
            yield return row;
            await Task.Yield();
        }
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        Deleted.Add(name);
        Files.Remove(name);
        return Task.CompletedTask;
    }
}

public sealed class FakeReceitaReleaseRepository : IReceitaReleaseRepository
{
    public List<string> Started { get; } = [];
    public List<ReceitaFileDigest> Files { get; } = [];
    public List<(string Release, string Status)> Progress { get; } = [];
    public List<(string Release, string Status, string? Notes)> Finished { get; } = [];
    public Dictionary<string, ReceitaReleaseSummary> Summaries { get; } = [];

    public Task<Guid> StartAsync(
        IUnitOfWork uow, Guid id, string release, string? sourceUri, CancellationToken ct = default)
    {
        Started.Add(release);
        return Task.FromResult(id);
    }

    public Task RecordFilesAsync(
        IUnitOfWork uow, string release, IReadOnlyList<ReceitaFileDigest> files, CancellationToken ct = default)
    {
        Files.AddRange(files);
        return Task.CompletedTask;
    }

    public Task RecordProgressAsync(
        IUnitOfWork uow, string release, string status,
        long establishmentsScanned, long establishmentsSelected, long companiesJoined, long partnersLoaded,
        Guid? batchId, CancellationToken ct = default)
    {
        Progress.Add((release, status));

        Summaries[release] = new ReceitaReleaseSummary
        {
            Id = Guid.NewGuid(),
            Release = release,
            Status = status,
            EstablishmentsScanned = establishmentsScanned,
            EstablishmentsSelected = establishmentsSelected,
            CompaniesJoined = companiesJoined,
            PartnersLoaded = partnersLoaded,
            BatchId = batchId,
            StartedAt = DateTimeOffset.UnixEpoch
        };

        return Task.CompletedTask;
    }

    public Task FinishAsync(
        IUnitOfWork uow, string release, string status, string? notes, CancellationToken ct = default)
    {
        Finished.Add((release, status, notes));
        return Task.CompletedTask;
    }

    public Task<ReceitaReleaseSummary?> GetAsync(string release, CancellationToken ct = default) =>
        Task.FromResult(Summaries.GetValueOrDefault(release));

    public Task<IReadOnlyList<ReceitaReleaseSummary>> ListAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReceitaReleaseSummary>>([.. Summaries.Values]);
}

public sealed class FakeMarketStatisticsRepository : IMarketStatisticsRepository
{
    public List<CnaeStatRow> ByCnae { get; private set; } = [];
    public List<MunicipioStatRow> ByMunicipio { get; private set; } = [];
    public Dictionary<string, string> MunicipioNames { get; private set; } = [];
    public int Writes { get; private set; }

    public Task ReplaceAsync(
        IUnitOfWork uow, string release,
        IReadOnlyList<CnaeStatRow> byCnae, IReadOnlyList<MunicipioStatRow> byMunicipio,
        IReadOnlyDictionary<string, string> municipioNames, CancellationToken ct = default)
    {
        Writes++;
        ByCnae = [.. byCnae];
        ByMunicipio = [.. byMunicipio];
        MunicipioNames = municipioNames.ToDictionary();

        return Task.CompletedTask;
    }

    public Task<long> CountEstablishmentsAsync(string release, CancellationToken ct = default) =>
        Task.FromResult(ByCnae.Sum(r => r.Establishments));
}

public sealed class FakeCompanyPartnerRepository : ICompanyPartnerRepository
{
    public List<CompanyPartnerRecord> Partners { get; } = [];

    public Task<int> UpsertAsync(
        IUnitOfWork uow, string release, IReadOnlyList<CompanyPartnerRecord> partners,
        CancellationToken ct = default)
    {
        Partners.AddRange(partners);
        return Task.FromResult(partners.Count);
    }

    public Task<IReadOnlyList<CompanyPartnerRecord>> ListByCnpjBasicoAsync(
        string cnpjBasico, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CompanyPartnerRecord>>(
            [.. Partners.Where(p => p.CnpjBasico == cnpjBasico)]);
}
