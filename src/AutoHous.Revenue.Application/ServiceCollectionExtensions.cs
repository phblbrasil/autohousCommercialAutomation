using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra os casos de uso que dependem apenas de persistencia.
    ///
    /// Duas ausencias deliberadas, pelo mesmo motivo: o caso de uso exige portas
    /// que so um host especifico compoe, e registra-lo aqui faria TODO host
    /// falhar na inicializacao por falta de uma dependencia que ele nunca usaria.
    ///
    /// - <see cref="ExecuteResearchRunUseCase"/> exige <c>IAgentRuntime</c>,
    ///   <c>IStructuredOutputValidator</c> e <c>IResearchPromptBuilder</c>: so o
    ///   worker os compoe. A API subir sem eles e correto — ela nunca executa
    ///   agente.
    /// - <see cref="PrepareReceitaReleaseUseCase"/> exige
    ///   <c>IReceitaSourceReader</c> e <c>IReceitaSpool</c>: so a CLI de captura
    ///   os compoe. A API nunca baixa 7 GB da Receita.
    /// </summary>
    public static IServiceCollection AddRevenueUseCases(this IServiceCollection services)
    {
        services.AddScoped<CreateAccountUseCase>();
        services.AddScoped<RequestAccountResearchUseCase>();
        services.AddScoped<IngestCompanyBatchUseCase>();
        services.AddScoped<IngestCompanyStreamUseCase>();
        services.AddScoped<ResolveAccountGraphUseCase>();
        services.AddScoped<DecideMergeCandidateUseCase>();
        services.AddScoped<ScoreAccountUseCase>();

        return services;
    }
}
