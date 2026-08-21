using AutoHous.Revenue.Agents;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Worker;

public static class WorkerServiceRegistration
{
    /// <summary>
    /// Composicao compartilhada entre o host do worker e os testes de integracao,
    /// para que o teste exercite exatamente a mesma arvore de dependencias que
    /// roda em producao.
    /// </summary>
    public static IServiceCollection AddRevenueWorker(
        this IServiceCollection services, IConfiguration configuration, string repositoryRoot)
    {
        var connectionString =
            configuration["REVENUE_DB_CONNECTION"]
            ?? Environment.GetEnvironmentVariable("REVENUE_DB_CONNECTION")
            ?? throw new InvalidOperationException("REVENUE_DB_CONNECTION nao configurada.");

        var runtimeName =
            configuration["AGENT_RUNTIME"]
            ?? Environment.GetEnvironmentVariable("AGENT_RUNTIME")
            ?? "fixture";

        var schemaPath = Path.Combine(repositoryRoot, "hermes", "schemas", "research-profile.schema.json");
        var promptPath = Path.Combine(repositoryRoot, "hermes", "prompts", "researcher.v1.md");

        services.AddRevenueInfrastructure(connectionString);
        services.AddRevenueUseCases();
        services.AddResearchValidator(schemaPath);

        services.AddAgentRuntime(
            runtimeName,
            configureHermes: o =>
            {
                o.BaseUrl = configuration["HERMES_BASE_URL"]
                    ?? Environment.GetEnvironmentVariable("HERMES_BASE_URL")
                    ?? o.BaseUrl;

                o.ApiKey = configuration["HERMES_API_SERVER_KEY"]
                    ?? Environment.GetEnvironmentVariable("HERMES_API_SERVER_KEY")
                    ?? string.Empty;
            },
            configureFixture: o =>
            {
                o.RootDirectory = configuration["AGENT_FIXTURE_DIR"]
                    ?? Environment.GetEnvironmentVariable("AGENT_FIXTURE_DIR")
                    ?? Path.Combine(repositoryRoot, "tests", "fixtures", "agent-runs");
            });

        services.AddResearchPrompts(promptPath, schemaPath);

        services.AddSingleton(new OutboxDispatcherOptions());
        services.AddScoped<ExecuteResearchRunUseCase>();
        services.AddSingleton<OutboxDispatcher>();

        return services;
    }
}
