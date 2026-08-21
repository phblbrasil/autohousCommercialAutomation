using AutoHous.Revenue.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.Agents;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o runtime de agente conforme AGENT_RUNTIME.
    ///
    /// Valor invalido falha na INICIALIZACAO, com mensagem clara. Cair
    /// silenciosamente no fixture em producao produziria pesquisas falsas com
    /// aparencia de sucesso.
    /// </summary>
    public static IServiceCollection AddAgentRuntime(
        this IServiceCollection services,
        string? runtimeName,
        Action<HermesOptions>? configureHermes = null,
        Action<FixtureAgentRuntimeOptions>? configureFixture = null)
    {
        var selected = (runtimeName ?? "fixture").Trim().ToLowerInvariant();

        switch (selected)
        {
            case "fixture":
                var fixtureOptions = new FixtureAgentRuntimeOptions();
                configureFixture?.Invoke(fixtureOptions);

                services.AddSingleton(fixtureOptions);
                services.AddSingleton<IAgentRuntime, FixtureAgentRuntime>();
                break;

            case "hermes":
                services.Configure<HermesOptions>(o => configureHermes?.Invoke(o));
                services.AddHttpClient<IAgentRuntime, HermesAgentRuntime>();
                break;

            default:
                throw new InvalidOperationException(
                    $"AGENT_RUNTIME='{runtimeName}' invalido. Use 'fixture' ou 'hermes'.");
        }

        return services;
    }

    public static IServiceCollection AddResearchValidator(this IServiceCollection services, string schemaPath)
    {
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException($"Schema do Research Profile nao encontrado: {schemaPath}");
        }

        services.AddSingleton(_ => StructuredOutputValidator.FromFile(schemaPath));
        services.AddSingleton<IStructuredOutputValidator>(
            sp => sp.GetRequiredService<StructuredOutputValidator>());

        return services;
    }

    /// <summary>
    /// Registra o construtor de prompt do Researcher. O prompt vem de arquivo
    /// versionado: agent_runs.prompt_version so tem valor se a versao for
    /// auditavel.
    /// </summary>
    public static IServiceCollection AddResearchPrompts(
        this IServiceCollection services, string promptPath, string schemaPath)
    {
        services.AddSingleton(new ResearchPromptBuilder(promptPath, schemaPath));
        services.AddSingleton<IResearchPromptBuilder>(
            sp => sp.GetRequiredService<ResearchPromptBuilder>());

        return services;
    }
}
