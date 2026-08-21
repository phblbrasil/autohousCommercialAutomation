using AutoHous.Revenue.Application;
using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Infrastructure;
using Dapper;
using Npgsql;

namespace AutoHous.Revenue.Integration.Tests;

public static class TestData
{
    /// <summary>Cria uma conta em 'discovered' e devolve o id.</summary>
    public static async Task<Guid> CreateAccountAsync(
        IAccountRepository accounts, string cnpj = "11222333000181", string name = "Grupo Vento Sul") =>
        await accounts.CreateFromCnpjAsync(cnpj, name, name, "SP", "Bauru");

    /// <summary>
    /// Enfileira um research.requested exatamente como a API faz, permitindo
    /// escolher o cenario de fixture.
    /// </summary>
    public static async Task<(Guid RunId, Guid EventId)> EnqueueResearchAsync(
        IUnitOfWorkFactory factory,
        IResearchRunRepository runs,
        IAccountRepository accounts,
        IOutboxRepository outbox,
        Guid accountId,
        AccountStatus currentStatus = AccountStatus.Discovered,
        string? scenario = null)
    {
        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        await using var uow = await factory.BeginAsync();

        await runs.CreateAsync(uow, runId, accountId, "standard");
        await accounts.TransitionAsync(uow, accountId, currentStatus, AccountStatus.Researching);

        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = eventId,
            EventType = EventTypes.ResearchRequested,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                research_run_id = runId,
                depth = "standard",
                fixture_scenario = scenario
            }),
            IdempotencyKey = IdempotencyKey.ForResearch(accountId, runId),
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        });

        await uow.CommitAsync();
        return (runId, eventId);
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
}
