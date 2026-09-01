using AutoHous.Revenue.Agents;
using AutoHous.Revenue.WebAudit;
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

        var auditSchemaPath = Path.Combine(repositoryRoot, "hermes", "schemas", "website-audit.schema.json");
        var auditPromptPath = Path.Combine(repositoryRoot, "hermes", "prompts", "website-auditor.v1.md");

        var pitchSchemaPath = Path.Combine(repositoryRoot, "hermes", "schemas", "product-pitch.schema.json");
        var pitchPromptPath = Path.Combine(repositoryRoot, "hermes", "prompts", "product-matcher.v1.md");

        var peopleSchemaPath = Path.Combine(repositoryRoot, "hermes", "schemas", "contact-discovery.schema.json");
        var peoplePromptPath = Path.Combine(repositoryRoot, "hermes", "prompts", "people-finder.v1.md");

        services.AddRevenueInfrastructure(connectionString);
        services.AddRevenueUseCases();

        // Um validador que conhece os QUATRO contratos. AddResearchValidator,
        // com schema unico, validaria a saida de cada agente contra o schema do
        // Researcher e a reprovaria inteira - com violacoes falando de campos
        // que aquele agente nunca deveria ter.
        //
        // A checagem de existencia dos arquivos acontece dentro de
        // AddAgentValidators, na composicao: schema faltando e erro de deploy, e
        // um deploy que sobe e so quebra no primeiro run de producao e a forma
        // cara de descobrir.
        services.AddAgentValidators(new Dictionary<Type, string>
        {
            [typeof(AutoHous.Revenue.Domain.Contracts.ResearchProfile)] = schemaPath,
            [typeof(AutoHous.Revenue.Domain.Contracts.WebsiteAuditProfile)] = auditSchemaPath,
            [typeof(AutoHous.Revenue.Domain.Contracts.ProductPitchProfile)] = pitchSchemaPath,
            [typeof(AutoHous.Revenue.Domain.Contracts.ContactDiscoveryProfile)] = peopleSchemaPath
        });

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
                var fixtureDir =
                    configuration["AGENT_FIXTURE_DIR"]
                    ?? Environment.GetEnvironmentVariable("AGENT_FIXTURE_DIR");

                // Caminho relativo se resolve contra a raiz do repositorio, nao
                // contra o diretorio de trabalho: `dotnet run --project src/...`
                // roda com cwd na pasta do projeto, e o AGENT_FIXTURE_DIR do
                // .env (relativo, como documentado) nao apontaria para lugar
                // nenhum - a pesquisa falharia com "Fixture nao encontrado".
                o.RootDirectory =
                    string.IsNullOrWhiteSpace(fixtureDir)
                        ? Path.Combine(repositoryRoot, "tests", "fixtures", "agent-runs")
                        : Path.IsPathRooted(fixtureDir)
                            ? fixtureDir
                            : Path.Combine(repositoryRoot, fixtureDir);
            });

        services.AddResearchPrompts(promptPath, schemaPath);
        services.AddWebsiteAuditPrompts(auditPromptPath, auditSchemaPath);
        services.AddProductPitchPrompts(pitchPromptPath, pitchSchemaPath);
        services.AddPeopleFinderPrompts(peoplePromptPath, peopleSchemaPath);

        // A sonda de site. Fica no worker e nao na API pelo mesmo motivo do
        // runtime de agente: a API nunca audita nada de forma sincrona.
        services.AddHttpWebsiteProbe();

        services.AddSingleton(new OutboxDispatcherOptions());

        // Os quatro casos de uso que rodam agente. Ficam aqui e nao no
        // AddRevenueUseCases pelo mesmo motivo de sempre: exigem IAgentRuntime e
        // os construtores de prompt, que so o worker compoe, e registra-los la
        // faria a API falhar na inicializacao por falta de algo que ela nunca
        // usaria.
        services.AddScoped<ExecuteResearchRunUseCase>();
        services.AddScoped<ExecuteWebsiteAuditUseCase>();
        services.AddScoped<MatchProductsUseCase>();
        services.AddScoped<ExecutePeopleFinderUseCase>();

        services.AddSingleton<OutboxDispatcher>();

        return services;
    }
}
