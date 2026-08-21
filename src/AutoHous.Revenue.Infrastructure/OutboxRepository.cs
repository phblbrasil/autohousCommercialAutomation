using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;
using Npgsql;

namespace AutoHous.Revenue.Infrastructure;

public sealed class OutboxRepository(NpgsqlConnectionFactory connections) : IOutboxRepository
{
    /// <summary>
    /// Insere o evento na mesma transacao da mudanca de estado que o originou.
    /// Conflito na chave de idempotencia e no-op: reenfileirar o mesmo trabalho
    /// nao pode gerar execucao (e cobranca de IA) duplicada.
    /// </summary>
    public async Task<Guid> EnqueueAsync(IUnitOfWork uow, OutboxEvent evt, CancellationToken ct = default)
    {
        const string sql = """
            insert into events_outbox
                (id, event_type, aggregate_type, aggregate_id, payload, idempotency_key, status, available_at)
            values
                (@Id, @EventType, @AggregateType, @AggregateId, @Payload::jsonb, @IdempotencyKey, @Status, @AvailableAt)
            on conflict (idempotency_key) do nothing
            returning id;
            """;

        var id = await uow.Db().ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new
            {
                evt.Id,
                evt.EventType,
                evt.AggregateType,
                evt.AggregateId,
                Payload = evt.PayloadJson,
                evt.IdempotencyKey,
                evt.Status,
                evt.AvailableAt
            }, uow.Tx(), cancellationToken: ct));

        return id ?? Guid.Empty;
    }

    /// <summary>
    /// Reivindica um lote de eventos pendentes.
    ///
    /// FOR UPDATE SKIP LOCKED e o que permite rodar varios workers sem que dois
    /// peguem o mesmo evento. Sem isso, a pesquisa seria executada - e paga -
    /// duas vezes. O blueprint descreve "claim event" sem especificar o mecanismo.
    /// </summary>
    public async Task<IReadOnlyList<OutboxEvent>> ClaimBatchAsync(int batchSize, CancellationToken ct = default)
    {
        const string sql = """
            update events_outbox
               set status = 'processing',
                   attempts = attempts + 1
             where id in (
                   select id
                     from events_outbox
                    where status = 'pending'
                      and available_at <= now()
                    order by available_at
                    limit @BatchSize
                      for update skip locked
             )
            returning id            as Id,
                      event_type    as EventType,
                      aggregate_type as AggregateType,
                      aggregate_id  as AggregateId,
                      payload::text as PayloadJson,
                      idempotency_key as IdempotencyKey,
                      status        as Status,
                      attempts      as Attempts,
                      available_at  as AvailableAt,
                      last_error    as LastError;
            """;

        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<OutboxEvent>(
            new CommandDefinition(sql, new { BatchSize = batchSize }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task MarkProcessedAsync(IUnitOfWork uow, Guid eventId, CancellationToken ct = default)
    {
        const string sql = """
            update events_outbox
               set status = 'processed',
                   processed_at = now(),
                   last_error = null
             where id = @EventId;
            """;

        await uow.Db().ExecuteAsync(
            new CommandDefinition(sql, new { EventId = eventId }, uow.Tx(), cancellationToken: ct));
    }

    /// <summary>
    /// Backoff exponencial com teto. Esgotadas as tentativas, o evento vai para
    /// 'dead' com o erro preservado - dead-letter em vez de retry infinito, para
    /// que um prompt quebrado nao queime orcamento de modelo indefinidamente.
    /// </summary>
    public async Task RescheduleAsync(
        Guid eventId, string error, int maxAttempts = 5, CancellationToken ct = default)
    {
        const string sql = """
            update events_outbox
               set status = case when attempts >= @MaxAttempts then 'dead' else 'pending' end,
                   available_at = now() + (least(power(2, attempts), 64) * interval '1 second'),
                   last_error = @Error
             where id = @EventId;
            """;

        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { EventId = eventId, Error = Truncate(error), MaxAttempts = maxAttempts },
            cancellationToken: ct));
    }

    private static string Truncate(string text, int max = 4000) =>
        text.Length <= max ? text : text[..max];
}
