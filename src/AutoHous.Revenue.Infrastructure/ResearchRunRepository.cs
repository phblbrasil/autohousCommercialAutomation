using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

public sealed class ResearchRunRepository(NpgsqlConnectionFactory connections) : IResearchRunRepository
{
    private const string SelectColumns = """
        id as Id, account_id as AccountId, run_type as RunType, status as Status,
        started_at as StartedAt, finished_at as FinishedAt, completeness as Completeness,
        result::text as ResultJson, error::text as ErrorJson
        """;

    public async Task CreateAsync(
        IUnitOfWork uow, Guid runId, Guid accountId, string runType, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into research_runs (id, account_id, run_type, status)
            values (@Id, @AccountId, @RunType, @Status)
            """,
            new { Id = runId, AccountId = accountId, RunType = runType, Status = RunStatus.Queued },
            uow.Tx(), cancellationToken: ct));

    public async Task<ResearchRun?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<ResearchRun>(new CommandDefinition(
            $"select {SelectColumns} from research_runs where id = @Id",
            new { Id = runId }, cancellationToken: ct));
    }

    public async Task CompleteAsync(
        IUnitOfWork uow, Guid runId, decimal completeness, string resultJson, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update research_runs
               set status = @Status, finished_at = now(),
                   completeness = @Completeness, result = @Result::jsonb, error = null
             where id = @Id
            """,
            new { Id = runId, Status = RunStatus.Completed, Completeness = completeness, Result = resultJson },
            uow.Tx(), cancellationToken: ct));

    /// <summary>
    /// Grava a falha FORA da transacao de persistencia: a transacao do run sofreu
    /// rollback, mas o motivo da falha precisa sobreviver para diagnostico.
    /// </summary>
    public async Task FailAsync(Guid runId, string errorJson, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition("""
            update research_runs
               set status = @Status, finished_at = now(), error = @Error::jsonb
             where id = @Id
            """,
            new { Id = runId, Status = RunStatus.Failed, Error = errorJson },
            cancellationToken: ct));
    }

    /// <summary>
    /// Base do cooldown do endpoint de pesquisa. Cooldown e regra de negocio e
    /// vive aqui - separado da idempotencia, que identifica a execucao.
    /// </summary>
    public async Task<ResearchRun?> LatestSuccessfulInMonthAsync(
        Guid accountId, DateTimeOffset reference, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<ResearchRun>(new CommandDefinition(
            $"""
             select {SelectColumns}
               from research_runs
              where account_id = @AccountId
                and status = @Status
                and date_trunc('month', started_at) = date_trunc('month', @Reference::timestamptz)
              order by started_at desc
              limit 1
             """,
            new { AccountId = accountId, Status = RunStatus.Completed, Reference = reference },
            cancellationToken: ct));
    }
}
