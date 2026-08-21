using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

public interface IResearchRunRepository
{
    Task CreateAsync(IUnitOfWork uow, Guid runId, Guid accountId, string runType, CancellationToken ct = default);
    Task<ResearchRun?> GetAsync(Guid runId, CancellationToken ct = default);
    Task CompleteAsync(IUnitOfWork uow, Guid runId, decimal completeness, string resultJson, CancellationToken ct = default);
    Task FailAsync(Guid runId, string errorJson, CancellationToken ct = default);
    Task<ResearchRun?> LatestSuccessfulInMonthAsync(Guid accountId, DateTimeOffset reference, CancellationToken ct = default);
}
