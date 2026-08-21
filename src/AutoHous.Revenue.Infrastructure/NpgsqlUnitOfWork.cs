using AutoHous.Revenue.Application;
using Npgsql;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Abre conexoes do pool. Separado da fabrica de unidade de trabalho porque
/// leitura nao precisa de transacao - e porque <see cref="IUnitOfWorkFactory"/>,
/// que e porta da Application, nao pode expor <c>NpgsqlConnection</c>.
/// </summary>
public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource)
{
    public Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        dataSource.OpenConnectionAsync(ct).AsTask();
}

public sealed class NpgsqlUnitOfWorkFactory(NpgsqlConnectionFactory connections) : IUnitOfWorkFactory
{
    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var connection = await connections.OpenAsync(ct);

        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new NpgsqlUnitOfWork(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Transacao explicita. A persistencia de um research profile e UMA transacao:
/// sources, evidence, signals, brands, locations, account, research_run,
/// agent_run e o evento de saida. Ou tudo, ou nada.
///
/// A conexao e a transacao sao <c>internal</c>: quem esta do lado de fora da
/// infraestrutura ve apenas <see cref="IUnitOfWork.CommitAsync"/>.
/// </summary>
internal sealed class NpgsqlUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction) : IUnitOfWork
{
    private bool _committed;

    internal NpgsqlConnection Connection { get; } = connection;
    internal NpgsqlTransaction Transaction { get; } = transaction;

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await Transaction.CommitAsync(ct);
        _committed = true;
    }

    /// <summary>
    /// Rollback implicito quando nao houve commit. E o que garante que uma
    /// excecao no meio da persistencia nao deixe escrita parcial.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            try
            {
                await Transaction.RollbackAsync();
            }
            catch (Exception)
            {
                // A conexao pode ja ter sido derrubada; o rollback e implicito.
            }
        }

        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal static class UnitOfWorkExtensions
{
    /// <summary>
    /// Recupera a transacao concreta a partir da porta.
    ///
    /// O cast e o preco de manter <c>NpgsqlConnection</c> fora do contrato da
    /// Application, e ele e seguro por construcao: a unica implementacao de
    /// <see cref="IUnitOfWork"/> registrada e <see cref="NpgsqlUnitOfWork"/>. A
    /// alternativa - expor a conexao na porta - devolveria um tipo de fornecedor
    /// ao nucleo, que e exatamente o que o §6.1 proibe.
    /// </summary>
    internal static NpgsqlConnection Db(this IUnitOfWork uow) => Unwrap(uow).Connection;

    internal static NpgsqlTransaction Tx(this IUnitOfWork uow) => Unwrap(uow).Transaction;

    private static NpgsqlUnitOfWork Unwrap(IUnitOfWork uow) =>
        uow as NpgsqlUnitOfWork
        ?? throw new InvalidOperationException(
            $"Esta infraestrutura exige uma unidade de trabalho Npgsql; recebeu {uow.GetType().Name}.");
}

/// <summary>Sonda de alcancabilidade usada pelo endpoint /health.</summary>
public sealed class PostgresHealthProbe(NpgsqlConnectionFactory connections) : IDatabaseHealthProbe
{
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await connections.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "select 1";
            await command.ExecuteScalarAsync(ct);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }
}
