using System.Reflection;
using DbUp;
using DbUp.Engine;

namespace AutoHous.Revenue.Migrator;

public static class Program
{
    public static int Main(string[] args)
    {
        var connectionString =
            args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? Environment.GetEnvironmentVariable("REVENUE_DB_CONNECTION");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "Connection string ausente. Defina REVENUE_DB_CONNECTION ou passe como argumento.");
            return 2;
        }

        var result = Run(connectionString);

        if (!result.Successful)
        {
            Console.Error.WriteLine($"FALHA em {result.ErrorScript?.Name}: {result.Error}");
            return 1;
        }

        Console.WriteLine($"OK - {result.Scripts.Count()} script(s) aplicado(s).");
        return 0;
    }

    /// <summary>
    /// Aplica as migrations pendentes. Exposto para os testes de integracao, que
    /// sobem um Postgres efemero e reusam exatamente este caminho de codigo em
    /// vez de recriar o schema por conta propria.
    /// </summary>
    public static DatabaseUpgradeResult Run(string connectionString)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        return DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.StartsWith("AutoHous.Revenue.Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .JournalToPostgresqlTable("public", "schema_versions")
            .LogToConsole()
            .Build()
            .PerformUpgrade();
    }
}
