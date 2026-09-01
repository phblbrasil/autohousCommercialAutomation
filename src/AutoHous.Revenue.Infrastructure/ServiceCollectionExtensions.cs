using AutoHous.Revenue.Application;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoHous.Revenue.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Ponto de composicao da persistencia: cada porta declarada na Application
    /// recebe aqui sua implementacao concreta. Nenhum outro lugar do sistema
    /// conhece <c>Npgsql</c>.
    /// </summary>
    public static IServiceCollection AddRevenueInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "REVENUE_DB_CONNECTION nao configurada. Ver .env.example.");
        }

        // NpgsqlDataSource como singleton: e ele que gerencia o pool.
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddSingleton<NpgsqlConnectionFactory>();

        services.AddSingleton<IUnitOfWorkFactory, NpgsqlUnitOfWorkFactory>();
        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<IResearchRunRepository, ResearchRunRepository>();
        services.AddSingleton<IAgentRunRepository, AgentRunRepository>();
        services.AddSingleton<IOutboxRepository, OutboxRepository>();
        services.AddSingleton<IResearchProfilePersister, ResearchProfilePersister>();
        services.AddSingleton<IWebsiteAuditPersister, WebsiteAuditPersister>();
        services.AddSingleton<ISearchRepository, SearchRepository>();
        services.AddSingleton<IEvidenceReadRepository, EvidenceReadRepository>();
        services.AddSingleton<IDatabaseHealthProbe, PostgresHealthProbe>();
        services.AddSingleton<IIngestionBatchRepository, IngestionBatchRepository>();
        services.AddSingleton<IAccountGraphRepository, AccountGraphRepository>();
        services.AddSingleton<IAccountScoreRepository, AccountScoreRepository>();
        services.AddSingleton<IProductFitRepository, ProductFitRepository>();
        services.AddSingleton<IProductFitPersister, ProductFitPersister>();
        services.AddSingleton<IContactPersister, ContactPersister>();
        services.AddSingleton<IAccountProgressRepository, AccountProgressRepository>();

        // Camada 01 — fonte oficial da Receita Federal (migrations 0013 e 0014).
        services.AddSingleton<IMarketStatisticsRepository, MarketStatisticsRepository>();
        services.AddSingleton<IReceitaReleaseRepository, ReceitaReleaseRepository>();
        services.AddSingleton<ICompanyPartnerRepository, CompanyPartnerRepository>();

        // Portas tecnicas: relogio e geracao de id. Registradas aqui porque a
        // Application declara o contrato e nao pode instanciar nada por si.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierGenerator, GuidV7Generator>();

        return services;
    }
}
