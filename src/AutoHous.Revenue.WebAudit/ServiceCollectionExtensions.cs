using AutoHous.Revenue.Application;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.WebAudit;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra a sonda HTTP como implementacao de <see cref="IWebsiteProbe"/>.
    ///
    /// Quando o headless browser existir, ele entra AQUI, atras da mesma porta -
    /// o caso de uso, o scoring e a persistencia nao mudam uma linha.
    /// </summary>
    public static IServiceCollection AddHttpWebsiteProbe(
        this IServiceCollection services, TimeSpan? timeout = null)
    {
        services.AddHttpClient<IWebsiteProbe, HttpWebsiteProbe>(client =>
        {
            // Curto de proposito. A auditoria roda em lote, e um site que leva
            // mais de 20s para o documento ja produziu a informacao que
            // interessa: ele e lento. Esperar dois minutos por ele so atrasaria
            // a fila para confirmar o que a medicao parcial ja diz.
            client.Timeout = timeout ?? TimeSpan.FromSeconds(20);

            // User-Agent de navegador real, e nao anonimo. Muito site brasileiro
            // fica atras de WAF que devolve 403 para cliente sem UA - e um 403
            // do WAF entraria na auditoria como "site fora do ar", reprovando em
            // Technology Pain uma conta cujo site esta perfeitamente no ar.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 AutoHousRevenueBot/1.0");

            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,

            // Redirect de dominio e comum e legitimo (www, http->https, troca de
            // marca). Acima disso costuma ser laco de consentimento ou geo, e
            // seguir mais nao melhora a medicao.
            MaxAutomaticRedirections = 5,

            AutomaticDecompression = System.Net.DecompressionMethods.All
        });

        return services;
    }
}
