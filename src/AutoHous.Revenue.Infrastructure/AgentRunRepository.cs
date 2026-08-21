using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Observabilidade da secao 28. "Custo de IA por conta pesquisada" - metrica
/// auxiliar da secao 1 e principal criterio para escalar de 1 para 10, 30, 100
/// contas - sai integralmente desta tabela.
/// </summary>
public sealed class AgentRunRepository(NpgsqlConnectionFactory connections) : IAgentRunRepository
{
    private const string InsertSql = """
        insert into agent_runs
            (id, account_id, research_run_id, agent_name, prompt_version, model_provider,
             model_name, external_run_id, status, input_tokens, output_tokens,
             estimated_cost, started_at, finished_at, error)
        values
            (@Id, @AccountId, @ResearchRunId, @AgentName, @PromptVersion, @ModelProvider,
             @ModelName, @ExternalRunId, @Status, @InputTokens, @OutputTokens,
             @EstimatedCost, @StartedAt, @FinishedAt, @ErrorJson::jsonb)
        """;

    private const string SelectColumns = """
        id as Id, account_id as AccountId, research_run_id as ResearchRunId,
        agent_name as AgentName, prompt_version as PromptVersion,
        model_provider as ModelProvider, model_name as ModelName,
        external_run_id as ExternalRunId, status as Status,
        input_tokens as InputTokens, output_tokens as OutputTokens,
        estimated_cost as EstimatedCost, started_at as StartedAt,
        finished_at as FinishedAt, error::text as ErrorJson
        """;

    public async Task InsertAsync(IUnitOfWork uow, AgentRun run, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(
            new CommandDefinition(InsertSql, run, uow.Tx(), cancellationToken: ct));

    /// <summary>
    /// Usado quando o run do agente falhou: a transacao de persistencia nao
    /// existe, mas o custo ja foi incorrido e precisa ser contabilizado.
    /// </summary>
    public async Task InsertOutsideTransactionAsync(AgentRun run, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(InsertSql, run, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AgentRun>> ListAsync(
        Guid? accountId, int limit, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<AgentRun>(new CommandDefinition(
            $"""
             select {SelectColumns}
               from agent_runs
              where (@AccountId::uuid is null or account_id = @AccountId)
              order by started_at desc
              limit @Limit
             """,
            new { AccountId = accountId, Limit = limit }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<decimal> TotalCostForAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "select coalesce(sum(estimated_cost), 0) from agent_runs where account_id = @AccountId",
            new { AccountId = accountId }, cancellationToken: ct));
    }
}
