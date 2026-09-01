using System.Runtime.CompilerServices;
using AutoHous.Revenue.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// Postgres 17 efemero por classe de teste. As migrations rodam pelo MESMO
/// caminho de codigo do migrator de producao - o schema testado e o schema real,
/// nao uma reconstrucao paralela que poderia divergir.
///
/// Por padrao o banco vem de um container (Testcontainers). Definindo
/// REVENUE_TEST_DB_CONNECTION - apontando para um servidor Postgres ja de pe, o
/// do docker compose por exemplo - ele passa a vir de um banco novo criado
/// nesse servidor. O isolamento e o mesmo nos dois casos: um banco exclusivo por
/// classe, destruido no fim. O que muda e quem hospeda.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string? ExternalServer =
        Environment.GetEnvironmentVariable("REVENUE_TEST_DB_CONNECTION");

    private readonly ITestDatabase _database;

    public PostgresFixture() =>
        _database = string.IsNullOrWhiteSpace(ExternalServer)
            ? CreateContainerDatabase()
            : new ExternalServerTestDatabase(ExternalServer);

    /// <summary>
    /// O JIT resolve os tipos de um metodo ao compilar ESSE metodo, antes de
    /// executar qualquer desvio. Enquanto Testcontainers era mencionado no
    /// proprio construtor, escolher o servidor externo nao adiantava nada: o
    /// Docker.DotNet.dll ja tinha sido carregado para compilar a linha.
    ///
    /// Isolar a mencao num metodo a parte, sem inline, e o que faz o caminho
    /// alternativo de fato nao tocar em Testcontainers.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ITestDatabase CreateContainerDatabase() => new ContainerTestDatabase();

    public string ConnectionString => _database.ConnectionString;

    public static string RepositoryRoot { get; } = FindRoot();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        var result = AutoHous.Revenue.Migrator.Program.Run(ConnectionString);

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migrations falharam em {result.ErrorScript?.Name}: {result.Error}");
        }
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    /// <summary>
    /// Constroi a arvore de dependencias real do worker apontando para o banco
    /// efemero, com o runtime de fixture.
    /// </summary>
    /// <summary>
    /// Compoe exatamente a arvore de dependencias do worker de producao, com um
    /// gancho para substituir o que nao pode rodar de verdade em teste.
    ///
    /// O gancho existe por causa da sonda de site: <c>AddRevenueWorker</c> registra
    /// a <c>HttpWebsiteProbe</c>, que faz HTTP real. Um teste de integracao que a
    /// usasse sairia para a internet - lento, nao deterministico, e dependente de
    /// um site de terceiro continuar no ar. Tudo o mais permanece o de producao:
    /// o ponto do teste e exercitar o mesmo caminho de codigo.
    /// </summary>
    public ServiceProvider BuildWorkerServices(Action<IServiceCollection>? overrides = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REVENUE_DB_CONNECTION"] = ConnectionString,
                ["AGENT_RUNTIME"] = "fixture",
                ["AGENT_FIXTURE_DIR"] = Path.Combine(RepositoryRoot, "tests", "fixtures", "agent-runs")
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRevenueWorker(configuration, RepositoryRoot);

        overrides?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "hermes", "schemas"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Raiz do repositorio nao encontrada.");
    }
}
