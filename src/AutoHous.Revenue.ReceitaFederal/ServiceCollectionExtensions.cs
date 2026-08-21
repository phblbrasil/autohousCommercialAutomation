using AutoHous.Revenue.Application;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.ReceitaFederal;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o acesso a fonte oficial da Receita Federal.
    ///
    /// Ponto de composicao unico da camada 01: nenhum outro lugar do sistema sabe
    /// que existe WebDAV, zip ou ISO-8859-1.
    /// </summary>
    public static IServiceCollection AddReceitaFederalSource(
        this IServiceCollection services, Action<ReceitaOptions>? configure = null)
    {
        services.Configure<ReceitaOptions>(o =>
        {
            // Ambiente antes do codigo: o token do compartilhamento muda quando a
            // Receita mexe na plataforma, e trocar variavel de ambiente nao exige
            // publicar versao nova.
            if (Environment.GetEnvironmentVariable(ReceitaOptions.EnvShareToken) is { Length: > 0 } token)
            {
                o.ShareToken = token;
            }

            if (Environment.GetEnvironmentVariable(ReceitaOptions.EnvCacheDir) is { Length: > 0 } cache)
            {
                o.CacheDirectory = cache;
            }

            configure?.Invoke(o);
        });

        services.AddHttpClient<IReceitaFederalArchive, ReceitaFederalArchive>((sp, client) =>
        {
            var options = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<ReceitaOptions>>().Value;

            client.Timeout = options.DownloadTimeout;
        });

        services.AddSingleton<ReceitaFileCache>();
        services.AddSingleton<IReceitaSourceReader, ReceitaSourceReader>();
        services.AddSingleton<IReceitaSpool, FileReceitaSpool>();

        // O caso de uso da carga entra AQUI e nao em AddRevenueUseCases: ele
        // depende das duas portas acima, e so este host as compoe. Registrado
        // junto delas, "quem pode carregar a Receita" e uma decisao unica em vez
        // de duas que precisam concordar.
        services.AddScoped<PrepareReceitaReleaseUseCase>();

        return services;
    }
}
