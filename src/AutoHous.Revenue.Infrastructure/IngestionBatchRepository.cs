using System.Text.Json;
using AutoHous.Revenue.Application;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Lotes de captura e linhas cruas (migration 0012).
/// </summary>
public sealed class IngestionBatchRepository(NpgsqlConnectionFactory connections) : IIngestionBatchRepository
{
    private const string SummaryColumns = """
        id                as Id,
        source_name       as SourceName,
        source_uri        as SourceUri,
        status            as Status,
        total_rows        as TotalRows,
        accepted_rows     as AcceptedRows,
        duplicate_rows    as DuplicateRows,
        rejected_rows     as RejectedRows,
        created_accounts  as CreatedAccounts,
        attached_cnpjs    as AttachedCnpjs,
        review_candidates as ReviewCandidates,
        notes             as Notes,
        started_at        as StartedAt,
        finished_at       as FinishedAt
        """;

    public async Task<Guid> OpenAsync(
        IUnitOfWork uow, Guid batchId, string sourceName, string? sourceUri, CancellationToken ct = default)
    {
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into ingestion_batches (id, source_name, source_uri, status)
            values (@Id, @SourceName, @SourceUri, @Status)
            """,
            new { Id = batchId, SourceName = sourceName, SourceUri = sourceUri, Status = IngestionBatchStatus.Open },
            uow.Tx(), cancellationToken: ct));

        return batchId;
    }

    /// <summary>
    /// Grava as linhas em um unico comando com <c>unnest</c>. Um INSERT por
    /// linha em um arquivo da Receita seria uma ida ao banco por CNPJ.
    ///
    /// <c>on conflict do nothing</c> sobre (batch_id, content_hash): reimportar
    /// o mesmo arquivo no mesmo lote e no-op, e a contagem devolvida diz quantas
    /// realmente entraram.
    /// </summary>
    public async Task<int> AppendRowsAsync(
        IUnitOfWork uow,
        Guid batchId,
        IReadOnlyList<(int RowNumber, RawCompanyRow Row, string ContentHash)> rows,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;

        var ids = new Guid[rows.Count];
        var numbers = new int[rows.Count];
        var payloads = new string[rows.Count];
        var cnpjs = new string?[rows.Count];
        var hashes = new string[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            var (rowNumber, row, hash) = rows[i];

            ids[i] = Guid.CreateVersion7();
            numbers[i] = rowNumber;
            payloads[i] = JsonSerializer.Serialize(row);
            cnpjs[i] = row.Cnpj;
            hashes[i] = hash;
        }

        return await uow.Db().ExecuteScalarAsync<int>(new CommandDefinition("""
            with entrada as (
                select unnest(@Ids::uuid[])   as id,
                       unnest(@Numbers::int[]) as row_number,
                       unnest(@Payloads::jsonb[]) as payload,
                       unnest(@Cnpjs::text[])  as cnpj_raw,
                       unnest(@Hashes::text[]) as content_hash
            ),
            inserido as (
                insert into companies_raw (id, batch_id, row_number, payload, cnpj_raw, content_hash)
                select id, @BatchId, row_number, payload, cnpj_raw, content_hash from entrada
                on conflict (batch_id, content_hash) do nothing
                returning 1
            )
            select count(*)::int from inserido
            """,
            new { BatchId = batchId, Ids = ids, Numbers = numbers, Payloads = payloads, Cnpjs = cnpjs, Hashes = hashes },
            uow.Tx(), cancellationToken: ct));
    }

    public async Task CloseCaptureAsync(
        IUnitOfWork uow, Guid batchId, int totalRows, int acceptedRows, int duplicateRows,
        CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update ingestion_batches
               set status = @Status,
                   total_rows = @TotalRows,
                   accepted_rows = @AcceptedRows,
                   duplicate_rows = @DuplicateRows
             where id = @Id
            """,
            new
            {
                Id = batchId,
                Status = IngestionBatchStatus.Captured,
                TotalRows = totalRows,
                AcceptedRows = acceptedRows,
                DuplicateRows = duplicateRows
            },
            uow.Tx(), cancellationToken: ct));

    public async Task<IReadOnlyList<PendingRawCompany>> ListPendingAsync(
        Guid batchId, int limit, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<(Guid Id, int RowNumber, string Payload)>(
            new CommandDefinition("""
                select id, row_number, payload::text
                  from companies_raw
                 where batch_id = @BatchId and status = @Status
                 order by row_number
                 limit @Limit
                """,
                new { BatchId = batchId, Status = RawCompanyStatus.Pending, Limit = limit },
                cancellationToken: ct));

        return [.. rows.Select(Materialize)];
    }

    public async Task<PendingRawCompany?> GetRawAsync(Guid rawId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<(Guid Id, int RowNumber, string Payload)?>(
            new CommandDefinition(
                "select id, row_number, payload::text from companies_raw where id = @Id",
                new { Id = rawId }, cancellationToken: ct));

        return row is { } found ? Materialize(found) : null;
    }

    public async Task MarkRowAsync(
        IUnitOfWork uow, Guid rawId, string status, string? rejectionReason, Guid? accountId,
        CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update companies_raw
               set status = @Status,
                   rejection_reason = @Reason,
                   account_id = @AccountId,
                   processed_at = now()
             where id = @Id
            """,
            new { Id = rawId, Status = status, Reason = rejectionReason, AccountId = accountId },
            uow.Tx(), cancellationToken: ct));

    public async Task RecordResolutionAsync(
        IUnitOfWork uow, Guid batchId, int rejected, int createdAccounts, int attachedCnpjs,
        int reviewCandidates, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update ingestion_batches
               set status = @Status,
                   rejected_rows = @Rejected,
                   created_accounts = @Created,
                   attached_cnpjs = @Attached,
                   review_candidates = @Review,
                   finished_at = now()
             where id = @Id
            """,
            new
            {
                Id = batchId,
                Status = IngestionBatchStatus.Resolved,
                Rejected = rejected,
                Created = createdAccounts,
                Attached = attachedCnpjs,
                Review = reviewCandidates
            },
            uow.Tx(), cancellationToken: ct));

    public async Task<IngestionBatchSummary?> GetAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<IngestionBatchSummary>(new CommandDefinition(
            $"select {SummaryColumns} from ingestion_batches where id = @Id",
            new { Id = batchId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IngestionBatchSummary>> ListAsync(int limit, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<IngestionBatchSummary>(new CommandDefinition(
            $"select {SummaryColumns} from ingestion_batches order by started_at desc limit @Limit",
            new { Limit = limit }, cancellationToken: ct));

        return rows.ToList();
    }

    private static PendingRawCompany Materialize((Guid Id, int RowNumber, string Payload) row) => new()
    {
        Id = row.Id,
        RowNumber = row.RowNumber,
        Row = JsonSerializer.Deserialize<RawCompanyRow>(row.Payload) ?? new RawCompanyRow()
    };
}
