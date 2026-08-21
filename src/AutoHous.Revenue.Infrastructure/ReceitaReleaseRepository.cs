using System.Text.Json;
using AutoHous.Revenue.Application;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>Lineage dos releases da Receita (migration 0013).</summary>
public sealed class ReceitaReleaseRepository(
    NpgsqlConnectionFactory connections) : IReceitaReleaseRepository
{
    private const string SummaryColumns = """
        id                      as Id,
        release                 as Release,
        source_uri              as SourceUri,
        status                  as Status,
        establishments_scanned  as EstablishmentsScanned,
        establishments_selected as EstablishmentsSelected,
        companies_joined        as CompaniesJoined,
        partners_loaded         as PartnersLoaded,
        batch_id                as BatchId,
        notes                   as Notes,
        started_at              as StartedAt,
        finished_at             as FinishedAt
        """;

    /// <summary>
    /// Abre - ou reabre - o registro do release.
    ///
    /// Recarregar a mesma competencia reinicia a linha em vez de criar outra: o
    /// dado da RF de agosto e um so, e duas linhas com contagens diferentes para
    /// "2026-08" nao teriam como ser desempatadas depois.
    /// </summary>
    public async Task<Guid> StartAsync(
        IUnitOfWork uow, Guid id, string release, string? sourceUri, CancellationToken ct = default)
    {
        return await uow.Db().ExecuteScalarAsync<Guid>(new CommandDefinition("""
            insert into receita_releases (id, release, source_uri, status)
            values (@Id, @Release, @SourceUri, @Status)
            on conflict (release) do update
                set status = excluded.status,
                    source_uri = coalesce(excluded.source_uri, receita_releases.source_uri),
                    started_at = now(),
                    finished_at = null,
                    notes = null
            returning id
            """,
            new
            {
                Id = id,
                Release = release,
                SourceUri = sourceUri,
                Status = ReceitaReleaseStatus.Downloading
            }, uow.Tx(), cancellationToken: ct));
    }

    public async Task RecordFilesAsync(
        IUnitOfWork uow, string release, IReadOnlyList<ReceitaFileDigest> files,
        CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update receita_releases set files = @Files::jsonb where release = @Release
            """,
            new { Release = release, Files = JsonSerializer.Serialize(files) },
            uow.Tx(), cancellationToken: ct));

    public async Task RecordProgressAsync(
        IUnitOfWork uow,
        string release,
        string status,
        long establishmentsScanned,
        long establishmentsSelected,
        long companiesJoined,
        long partnersLoaded,
        Guid? batchId,
        CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update receita_releases
               set status = @Status,
                   establishments_scanned = @Scanned,
                   establishments_selected = @Selected,
                   companies_joined = @Joined,
                   partners_loaded = @Partners,
                   batch_id = coalesce(@BatchId, batch_id)
             where release = @Release
            """,
            new
            {
                Release = release,
                Status = status,
                Scanned = establishmentsScanned,
                Selected = establishmentsSelected,
                Joined = companiesJoined,
                Partners = partnersLoaded,
                BatchId = batchId
            }, uow.Tx(), cancellationToken: ct));

    public async Task FinishAsync(
        IUnitOfWork uow, string release, string status, string? notes, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update receita_releases
               set status = @Status, notes = @Notes, finished_at = now()
             where release = @Release
            """,
            new { Release = release, Status = status, Notes = notes },
            uow.Tx(), cancellationToken: ct));

    public async Task<ReceitaReleaseSummary?> GetAsync(string release, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<ReceitaReleaseSummary>(new CommandDefinition(
            $"select {SummaryColumns} from receita_releases where release = @Release",
            new { Release = release }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ReceitaReleaseSummary>> ListAsync(
        int limit, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<ReceitaReleaseSummary>(new CommandDefinition(
            $"select {SummaryColumns} from receita_releases order by release desc limit @Limit",
            new { Limit = limit }, cancellationToken: ct));

        return rows.ToList();
    }
}
