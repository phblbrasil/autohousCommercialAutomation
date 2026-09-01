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
    /// <summary>
    /// Tentativas ao abrir conexao, com espera crescente entre elas.
    ///
    /// Nao e hardening especulativo. A primeira carga nacional completa morreu
    /// exatamente aqui, depois de quase 12 horas e 47% do lote:
    ///
    ///     Npgsql.NpgsqlException: The operation has timed out
    ///       at NpgsqlConnector.ConnectAsync -> RawOpen -> OpenNewConnector
    ///       at ResolveAccountGraphUseCase.CreateAsync
    ///
    /// A causa foi ambiental e esta no log de eventos do Windows: a maquina
    /// entrou em Modern Standby a noite inteira, e o processo morreu DOIS
    /// SEGUNDOS depois de ela acordar - as conexoes do pool tinham morrido junto
    /// e a primeira reconexao estourou o timeout.
    ///
    /// O defeito real nao foi o soluco: foi um job de horas nao sobreviver a UM
    /// soluco. Suspensao, reinicio de container e blip de rede sao normais em
    /// maquina de desenvolvimento, e serao normais tambem no Railway, onde um
    /// deploy do Postgres derruba conexao por alguns segundos.
    /// </summary>
    private const int MaxAttempts = 5;

    /// <param name="retryTransient">
    /// Falso para quem precisa de resposta rapida sobre o estado do banco. A
    /// sonda de <c>/health</c> e o caso: insistir por 15 segundos faria uma
    /// liveness probe expirar e o orquestrador reiniciar um servico que estava
    /// apenas esperando o banco voltar - transformando um soluco de conexao num
    /// ciclo de restart.
    /// </param>
    public async Task<NpgsqlConnection> OpenAsync(
        CancellationToken ct = default, bool retryTransient = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dataSource.OpenConnectionAsync(ct);
            }
            catch (Exception ex) when (retryTransient && attempt < MaxAttempts
                                       && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                // 1s, 2s, 4s, 8s. O teto de ~15s cobre com folga o tempo que o
                // Docker Desktop leva para reprojetar a porta depois de a
                // maquina acordar, que foi o caso observado.
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    /// <summary>
    /// O Npgsql ja classifica o que e transitorio em <c>IsTransient</c> - timeout,
    /// falha de rede, servidor em recuperacao. Confiar nessa classificacao em vez
    /// de listar codigos evita que a lista envelheca; o TimeoutException entra
    /// separado porque chega como inner de uma NpgsqlException so as vezes.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,
        TimeoutException => true,
        _ => false
    };
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
            await using var connection = await connections.OpenAsync(ct, retryTransient: false);
            await using var command = connection.CreateCommand();
            command.CommandText = "select 1";
            await command.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }
}
