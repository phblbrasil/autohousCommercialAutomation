using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

public interface IAgentRunRepository
{
    Task InsertAsync(IUnitOfWork uow, AgentRun run, CancellationToken ct = default);
    Task InsertOutsideTransactionAsync(AgentRun run, CancellationToken ct = default);
    Task<IReadOnlyList<AgentRun>> ListAsync(Guid? accountId, int limit, CancellationToken ct = default);
    Task<decimal> TotalCostForAccountAsync(Guid accountId, CancellationToken ct = default);
}
