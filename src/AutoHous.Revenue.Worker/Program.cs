using AutoHous.Revenue.Worker;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Services.AddSerilog();

// A raiz do repositorio localiza prompt, schema e fixtures. Em container,
// definir REPOSITORY_ROOT explicitamente.
var repositoryRoot =
    Environment.GetEnvironmentVariable("REPOSITORY_ROOT")
    ?? FindRepositoryRoot(AppContext.BaseDirectory);

builder.Services.AddRevenueWorker(builder.Configuration, repositoryRoot);
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());

var host = builder.Build();
await host.RunAsync();

static string FindRepositoryRoot(string start)
{
    var dir = new DirectoryInfo(start);

    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "hermes", "schemas")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException(
        $"Raiz do repositorio nao encontrada a partir de {start}. Defina REPOSITORY_ROOT.");
}
