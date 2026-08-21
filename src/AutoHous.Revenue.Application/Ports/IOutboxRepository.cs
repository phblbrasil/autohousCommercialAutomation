using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

public interface IOutboxRepository
{
    Task<Guid> EnqueueAsync(IUnitOfWork uow, OutboxEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxEvent>> ClaimBatchAsync(int batchSize, CancellationToken ct = default);
    Task MarkProcessedAsync(IUnitOfWork uow, Guid eventId, CancellationToken ct = default);
    Task RescheduleAsync(Guid eventId, string error, int maxAttempts = 5, CancellationToken ct = default);
}
