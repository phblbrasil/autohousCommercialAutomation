using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public sealed record IngestCompanyBatchCommand
{
    public required string SourceName { get; init; }
    public string? SourceUri { get; init; }
    public required IReadOnlyList<RawCompanyRow> Rows { get; init; }
}

public sealed record IngestCompanyBatchResult
{
    public required Guid BatchId { get; init; }
    public required int TotalRows { get; init; }
    public required int AcceptedRows { get; init; }
    public required int DuplicateRows { get; init; }
}

/// <summary>
/// Etapa 01 do pipeline: capturar. Este caso de uso NAO interpreta nada — nao
/// valida CNAE, nao valida CNPJ, nao decide grupo economico.
///
/// A separacao e deliberada. A normalizacao vai errar em algum momento: um CNAE
/// novo, um municipio grafado diferente, um encoding inesperado. Se a linha
/// original nao existir no banco, corrigir a regra exige reimportar a fonte — e
/// bases empresariais mudam entre uma carga e outra, entao o dado de ontem nao
/// volta. Com a linha crua guardada, corrigir e reprocessar.
///
/// Recebe a lista inteira e grava em UMA transacao: e o caminho de listas
/// pequenas — <c>POST /ingestion/batches</c>, integracao programatica, teste.
/// Para volume vindo da base da Receita, use
/// <see cref="IngestCompanyStreamUseCase"/>: com centenas de milhares de linhas,
/// uma transacao unica segura locks por minutos e perde tudo em qualquer erro.
/// </summary>
public sealed class IngestCompanyBatchUseCase(
    IIngestionBatchRepository batches,
    IUnitOfWorkFactory unitOfWork,
    IIdentifierGenerator ids,
    ILogger<IngestCompanyBatchUseCase> logger)
{
    public async Task<IngestCompanyBatchResult> ExecuteAsync(
        IngestCompanyBatchCommand command, CancellationToken ct = default)
    {
        var batchId = ids.NewId();

        var hashed = command.Rows
            .Select((row, index) => (RowNumber: index + 1, Row: row, ContentHash: RawCompanyRowHash.Of(row)))
            .ToList();

        // Deduplicacao dentro do proprio arquivo, antes do banco: um CSV com a
        // mesma linha repetida nao deve gastar dois INSERT so para o indice
        // unico rejeitar o segundo.
        var deduped = hashed
            .GroupBy(r => r.ContentHash)
            .Select(g => g.First())
            .ToList();

        await using var uow = await unitOfWork.BeginAsync(ct);

        await batches.OpenAsync(uow, batchId, command.SourceName, command.SourceUri, ct);
        var inserted = await batches.AppendRowsAsync(uow, batchId, deduped, ct);

        var duplicates = hashed.Count - inserted;

        await batches.CloseCaptureAsync(uow, batchId, hashed.Count, inserted, duplicates, ct);
        await uow.CommitAsync(ct);

        logger.LogInformation(
            "Lote {BatchId} de '{Source}': {Total} linha(s), {Inserted} gravada(s), {Duplicates} duplicada(s).",
            batchId, command.SourceName, hashed.Count, inserted, duplicates);

        return new IngestCompanyBatchResult
        {
            BatchId = batchId,
            TotalRows = hashed.Count,
            AcceptedRows = inserted,
            DuplicateRows = duplicates
        };
    }
}
