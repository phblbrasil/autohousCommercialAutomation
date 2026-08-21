using AutoHous.Revenue.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// Postgres 17 efemero por classe de teste. As migrations rodam pelo MESMO
/// caminho de codigo do migrator de producao - o schema testado e o schema real,
/// nao uma reconstrucao paralela que poderia divergir.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("revenue_test")
        .WithUsername("revenue")
        .WithPassword("revenue")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public static string RepositoryRoot { get; } = FindRoot();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var result = AutoHous.Revenue.Migrator.Program.Run(ConnectionString);

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migrations falharam em {result.ErrorScript?.Name}: {result.Error}");
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>
    /// Constroi a arvore de dependencias real do worker apontando para o banco
    /// efemero, com o runtime de fixture.
    /// </summary>
    public ServiceProvider BuildWorkerServices()
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
