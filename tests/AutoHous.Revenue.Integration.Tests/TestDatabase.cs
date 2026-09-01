using Npgsql;
using Testcontainers.PostgreSql;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// De onde vem o Postgres de uma classe de teste. Duas implementacoes, mesmo
/// contrato: um banco vazio e exclusivo, que morre com a classe.
/// </summary>
internal interface ITestDatabase : IAsyncDisposable
{
    string ConnectionString { get; }

    ValueTask StartAsync();
}

/// <summary>
/// O caminho padrao: um container efemero por classe de teste, sem depender de
/// nada instalado na maquina alem do Docker.
/// </summary>
internal sealed class ContainerTestDatabase : ITestDatabase
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("revenue_test")
        .WithUsername("revenue")
        .WithPassword("revenue")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask StartAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// O caminho alternativo: um banco novo dentro de um servidor Postgres que ja
/// esta de pe - o do docker compose, tipicamente.
///
/// Existe para ambientes onde o Testcontainers nao pode rodar com o Docker
/// saudavel do lado. Sob o Smart App Control do Windows, por exemplo, o
/// Docker.DotNet.dll e bloqueado no load e a bateria inteira falha antes do
/// primeiro teste.
/// </summary>
internal sealed class ExternalServerTestDatabase : ITestDatabase
{
    public ExternalServerTestDatabase(string server)
    {
        // Nome unico por instancia. Duas classes de teste no mesmo servidor nao
        // podem dividir banco: uma truncaria a tabela que a outra esta lendo.
        ConnectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = $"revenue_test_{Guid.NewGuid():n}"
        }.ConnectionString;
    }

    public string ConnectionString { get; }

    /// <summary>
    /// Nada a subir: quem cria o banco e o EnsureDatabase do proprio migrator,
    /// no mesmo passo que aplica as migrations.
    /// </summary>
    public ValueTask StartAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Aqui o banco nao vai embora junto com um container: quem criou derruba.
        // WITH (FORCE) encerra as conexoes que sobraram no pool - sem ele o DROP
        // falha com "database is being accessed by other users" e o servidor
        // acumula um banco de teste por execucao.
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString);
        var database = admin.Database!;

        admin.Database = "postgres";

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        await using var drop = new NpgsqlCommand(
            $"""drop database if exists "{database}" with (force)""", connection);

        await drop.ExecuteNonQueryAsync();
    }
}
