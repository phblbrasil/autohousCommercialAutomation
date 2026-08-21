using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public sealed record IngestCompanyStreamCommand
{
    public required string SourceName { get; init; }
    public string? SourceUri { get; init; }
    public required IAsyncEnumerable<RawCompanyRow> Rows { get; init; }

    /// <summary>
    /// Quantas linhas por transacao. 5.000 e o ponto em que o custo de ida ao
    /// banco ja diluiu e a transacao ainda dura menos de um segundo.
    /// </summary>
    public int ChunkSize { get; init; } = 5_000;

    /// <summary>Id do lote, quando o chamador precisa conhece-lo antes (carga da RF).</summary>
    public Guid? BatchId { get; init; }
}

/// <summary>
/// Etapa 01 do pipeline, em stream: capturar sem nunca ter o arquivo inteiro em
/// memoria.
///
/// Mesma politica do <see cref="IngestCompanyBatchUseCase"/> - nao interpreta
/// nada, nao valida CNAE, nao decide grupo economico - com uma diferenca de
/// mecanica que so aparece em escala: uma transacao por bloco, e nao uma para o
/// lote inteiro. A base da Receita entrega centenas de milhares de linhas do
/// universo automotivo; numa transacao unica isso seguraria locks por minutos e
/// perderia todo o trabalho em qualquer erro.
///
/// O lote termina em <c>captured</c>, exatamente como no caminho de lista: quem
/// resolve o grafo continua sendo <see cref="ResolveAccountGraphUseCase"/>.
/// </summary>
public sealed class IngestCompanyStreamUseCase(
    IIngestionBatchRepository batches,
    IUnitOfWorkFactory unitOfWork,
    IIdentifierGenerator ids,
    ILogger<IngestCompanyStreamUseCase> logger)
{
    public async Task<IngestCompanyBatchResult> ExecuteAsync(
        IngestCompanyStreamCommand command, CancellationToken ct = default)
    {
        if (command.ChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command), command.ChunkSize, "ChunkSize deve ser positivo.");
        }

        var batchId = command.BatchId ?? ids.NewId();

        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            await batches.OpenAsync(uow, batchId, command.SourceName, command.SourceUri, ct);
            await uow.CommitAsync(ct);
        }

        // Deduplicacao dentro do proprio arquivo, antes do banco: uma linha
        // repetida na origem nao deve gastar um INSERT so para o indice unico
        // rejeitar o segundo.
        //
        // O conjunto e limitado ao que sobrevive ao filtro de origem - centenas
        // de milhares de hashes, dezenas de MB -, e nao aos 63 milhoes de linhas
        // que a base da Receita tem.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chunk = new List<(int RowNumber, RawCompanyRow Row, string ContentHash)>(command.ChunkSize);

        int total = 0, inserted = 0;

        await foreach (var row in command.Rows.WithCancellation(ct))
        {
            total++;

            var hash = RawCompanyRowHash.Of(row);

            if (!seen.Add(hash)) continue;

            chunk.Add((total, row, hash));

            if (chunk.Count < command.ChunkSize) continue;

            inserted += await FlushAsync(batchId, chunk, ct);
            chunk.Clear();
        }

        if (chunk.Count > 0)
        {
            inserted += await FlushAsync(batchId, chunk, ct);
        }

        // Duplicata e o que foi lido e nao entrou - inclusive o que o banco
        // recusou por (batch_id, content_hash) ao reprocessar o mesmo lote.
        // Contar so o que a memoria pegou esconderia a segunda metade.
        var duplicates = total - inserted;

        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            await batches.CloseCaptureAsync(uow, batchId, total, inserted, duplicates, ct);
            await uow.CommitAsync(ct);
        }

        logger.LogInformation(
            "Lote {BatchId} de '{Source}': {Total} linha(s), {Inserted} gravada(s), {Duplicates} duplicada(s).",
            batchId, command.SourceName, total, inserted, duplicates);

        return new IngestCompanyBatchResult
        {
            BatchId = batchId,
            TotalRows = total,
            AcceptedRows = inserted,
            DuplicateRows = duplicates
        };
    }

    private async Task<int> FlushAsync(
        Guid batchId,
        IReadOnlyList<(int RowNumber, RawCompanyRow Row, string ContentHash)> chunk,
        CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);

        var written = await batches.AppendRowsAsync(uow, batchId, chunk, ct);
        await uow.CommitAsync(ct);

        return written;
    }
}
