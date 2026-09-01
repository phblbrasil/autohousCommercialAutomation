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
    /// Registra UM validador que conhece o schema de cada contrato.
    ///
    /// Substitui <see cref="AddResearchValidator"/> nos hosts que rodam mais de
    /// um agente. A checagem de existencia acontece aqui, na composicao, e nao
    /// na primeira validacao: schema faltando e erro de deploy, e deploy que
    /// sobe e so quebra no primeiro run de producao e a forma cara de descobrir.
    /// </summary>
    public static IServiceCollection AddAgentValidators(
        this IServiceCollection services, IReadOnlyDictionary<Type, string> schemaPathsByContract)
    {
        foreach (var (contract, path) in schemaPathsByContract)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Schema de {contract.Name} nao encontrado: {path}");
            }
        }

        services.AddSingleton(_ => StructuredOutputValidator.FromFiles(schemaPathsByContract));
        services.AddSingleton<IStructuredOutputValidator>(
            sp => sp.GetRequiredService<StructuredOutputValidator>());

        return services;
    }

    /// <summary>
    /// Registra o construtor de prompt do Website Auditor. Mesmo motivo do
    /// Researcher para vir de arquivo: <c>agent_runs.prompt_version</c> so tem
    /// valor se a versao for auditavel.
    /// </summary>
    public static IServiceCollection AddWebsiteAuditPrompts(
        this IServiceCollection services, string promptPath, string schemaPath)
    {
        services.AddSingleton(new WebsiteAuditPromptBuilder(promptPath, schemaPath));
        services.AddSingleton<IWebsiteAuditPromptBuilder>(
            sp => sp.GetRequiredService<WebsiteAuditPromptBuilder>());

        return services;
    }

    /// <summary>
    /// Registra o construtor de prompt do Product Matcher (A04). Mesmo motivo
    /// dos anteriores para vir de arquivo.
    /// </summary>
    public static IServiceCollection AddProductPitchPrompts(
        this IServiceCollection services, string promptPath, string schemaPath)
    {
        services.AddSingleton(new ProductPitchPromptBuilder(promptPath, schemaPath));
        services.AddSingleton<IProductPitchPromptBuilder>(
            sp => sp.GetRequiredService<ProductPitchPromptBuilder>());

        return services;
    }

    /// <summary>
    /// Registra o construtor de prompt do People Finder (A05).
    /// </summary>
    public static IServiceCollection AddPeopleFinderPrompts(
        this IServiceCollection services, string promptPath, string schemaPath)
    {
        services.AddSingleton(new PeopleFinderPromptBuilder(promptPath, schemaPath));
        services.AddSingleton<IPeopleFinderPromptBuilder>(
            sp => sp.GetRequiredService<PeopleFinderPromptBuilder>());

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
