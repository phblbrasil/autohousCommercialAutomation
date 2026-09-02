using System.Net;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.WebAudit;

namespace AutoHous.Revenue.WebAudit.Tests;

/// <summary>
/// A sonda de site contra HTML controlado.
///
/// Este projeto não existia. Toda a suíte usava <c>StubProbe</c>, então a única
/// peça que realmente lê HTML — e a única que encosta na internet aberta — era a
/// única sem teste nenhum. É o mesmo formato de lacuna dos persisters de A04 e
/// A05: o código que toca o mundo bagunçado é o que fica descoberto, porque
/// testá-lo dá mais trabalho.
///
/// Os testes fixam o comportamento ATUAL, inclusive onde ele está errado — os
/// casos assim estão marcados. É o que permite trocar o motor de parse (regex →
/// parser de HTML de verdade) sabendo o que muda e por quê, em vez de descobrir
/// em produção.
/// </summary>
public class HttpWebsiteProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>
    /// Serve HTML por caminho, sem tocar a rede. A sonda pede a página, e depois
    /// <c>/robots.txt</c> e <c>/sitemap.xml</c> — quem não estiver no mapa
    /// responde 404, que é o que um site sem esses arquivos faz.
    /// </summary>
    private sealed class MapHandler(Dictionary<string, string> rotas) : HttpMessageHandler
    {
        public List<string> Pedidos { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Pedidos.Add(path);

            if (!rotas.TryGetValue(path, out var corpo))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                    Content = new StringContent(string.Empty)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(corpo, System.Text.Encoding.UTF8, "text/html")
            });
        }
    }

    private static async Task<WebsiteProbeResult> ProbeAsync(
        string html, Dictionary<string, string>? extras = null)
    {
        var rotas = new Dictionary<string, string> { ["/"] = html };

        foreach (var (k, v) in extras ?? []) rotas[k] = v;

        using var handler = new MapHandler(rotas);
        using var client = new HttpClient(handler);

        var probe = new HttpWebsiteProbe(client, new FixedClock(Now));

        return await probe.ProbeAsync("https://exemplo.com.br", TestContext.Current.CancellationToken);
    }

    private const string PaginaCompleta = """
        <!doctype html>
        <html lang="pt-BR">
        <head>
          <title>Grupo Vento Sul — Seminovos</title>
          <meta name="description" content="Seminovos com procedência em Porto Alegre.">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <link rel="canonical" href="https://exemplo.com.br/">
          <script type="application/ld+json">{"@context":"https://schema.org","@type":"AutoDealer","name":"Grupo Vento Sul"}</script>
          <link rel="stylesheet" href="/estilo.css">
          <script src="/app.js"></script>
        </head>
        <body>
          <h1>Seminovos</h1>
          <img src="/carro.webp" alt="Onix 2022" width="400" height="300" loading="lazy">
          <a href="/estoque">Estoque</a>
        </body>
        </html>
        """;

    // ------------------------------------------------------------- o caminho feliz

    [Fact]
    public async Task Pagina_completa_mede_desempenho_seo_e_mobile()
    {
        var r = await ProbeAsync(PaginaCompleta);

        Assert.True(r.Reached);
        Assert.Equal(200, r.StatusCode);
        Assert.Equal(Now, r.ObservedAt);

        // Desempenho: medido, não estimado.
        Assert.NotNull(r.TimeToFirstByte);
        Assert.NotNull(r.DocumentLoadTime);
        Assert.True(r.DocumentBytes > 0);

        // SEO no head.
        Assert.True(r.IsHttps);
        Assert.True(r.HasTitle);
        Assert.True(r.HasMetaDescription);
        Assert.True(r.HasH1);
        Assert.True(r.HasCanonical);
        Assert.True(r.HasStructuredData);

        // Mobile.
        Assert.True(r.HasViewportMeta);
        Assert.False(r.HasFixedWidthViewport);

        // Um script síncrono no head e uma folha de estilo.
        Assert.Equal(2, r.RenderBlockingResources);
    }

    [Fact]
    public async Task Pagina_vazia_reporta_ausencia_e_nao_nulo()
    {
        var r = await ProbeAsync("<html><body><p>oi</p></body></html>");

        // Ausência medida é `false`; só o que não deu para medir vira nulo. A
        // distinção é o que impede o scoring de tratar "não sei" como "está bom".
        Assert.False(r.HasTitle);
        Assert.False(r.HasMetaDescription);
        Assert.False(r.HasH1);
        Assert.False(r.HasCanonical);
        Assert.False(r.HasStructuredData);
        Assert.False(r.HasViewportMeta);
        Assert.Equal(0, r.RenderBlockingResources);
    }

    [Fact]
    public async Task Viewport_de_largura_fixa_e_o_oposto_de_responsivo()
    {
        var r = await ProbeAsync(
            """<html><head><meta name="viewport" content="width=1024"></head><body></body></html>""");

        Assert.True(r.HasViewportMeta);
        Assert.True(r.HasFixedWidthViewport);
    }

    // ----------------------------------------------------------- arquivos de raiz

    [Fact]
    public async Task Robots_e_sitemap_sao_verificados_na_raiz()
    {
        var r = await ProbeAsync(PaginaCompleta, new Dictionary<string, string>
        {
            ["/robots.txt"] = "User-agent: *\nAllow: /",
            ["/sitemap.xml"] = "<urlset></urlset>"
        });

        Assert.True(r.HasRobotsTxt);
        Assert.True(r.HasSitemap);
    }

    [Fact]
    public async Task Sem_robots_nem_sitemap_a_ausencia_e_registrada()
    {
        var r = await ProbeAsync(PaginaCompleta);

        Assert.False(r.HasRobotsTxt);
        Assert.False(r.HasSitemap);
    }

    // ----------------------------------------------------------------- falhas

    [Fact]
    public async Task Erro_do_servidor_nao_gasta_requisicao_extra()
    {
        using var handler = new MapHandler([]);
        using var client = new HttpClient(handler);

        var probe = new HttpWebsiteProbe(client, new FixedClock(Now));
        var r = await probe.ProbeAsync("https://exemplo.com.br", TestContext.Current.CancellationToken);

        Assert.Equal(404, r.StatusCode);
        Assert.False(r.Reached);
        Assert.NotNull(r.Error);

        // Só a página. Não vale gastar robots e sitemap sobre um site quebrado.
        Assert.Single(handler.Pedidos);

        // E o desempenho continua medido: 404 lento é um achado.
        Assert.NotNull(r.TimeToFirstByte);
    }

    // ------------------------------------------------- o que o regex erra hoje

    /// <summary>
    /// **Comportamento errado, fixado de propósito.**
    ///
    /// `H1Regex` é `&lt;h1[^&gt;]*&gt;` aplicado ao documento cru, então um `h1`
    /// dentro de comentário HTML conta como `h1` de verdade. Um parser não
    /// cometeria esse erro — comentário não é elemento.
    ///
    /// O teste existe para que a troca do motor de parse mostre a mudança em vez
    /// de escondê-la: quando ele quebrar, é porque o defeito foi corrigido, e a
    /// linha vira `Assert.False`.
    /// </summary>
    [Fact]
    public async Task DEFEITO_h1_dentro_de_comentario_conta_como_h1()
    {
        var r = await ProbeAsync(
            "<html><head><title>x</title></head><body><!-- <h1>antigo</h1> --><p>sem titulo</p></body></html>");

        Assert.True(r.HasH1);
    }

    /// <summary>
    /// **Comportamento errado, fixado de propósito.**
    ///
    /// A assinatura de dado estruturado casa com o texto `application/ld+json`
    /// em qualquer lugar do documento — inclusive dentro de um comentário, ou
    /// citado como texto num artigo sobre SEO. Um parser leria os elementos
    /// `script` de verdade.
    /// </summary>
    [Fact]
    public async Task DEFEITO_mencao_a_ld_json_em_comentario_conta_como_dado_estruturado()
    {
        var r = await ProbeAsync(
            "<html><head><title>x</title></head><body><!-- usar application/ld+json aqui --></body></html>");

        Assert.True(r.HasStructuredData);
    }

    /// <summary>
    /// **Limite conhecido.** `HasStructuredData` é booleano: não distingue um
    /// rodapé com `Organization` de uma vitrine inteira marcada com `Vehicle` e
    /// `Offer`. É essa segunda que torna o estoque citável por um motor de
    /// resposta, e hoje as duas são o mesmo `true`.
    /// </summary>
    [Fact]
    public async Task LIMITE_dado_estruturado_e_booleano_e_nao_diz_o_tipo()
    {
        var comOrganization = await ProbeAsync("""
            <html><head><title>x</title>
            <script type="application/ld+json">{"@type":"Organization"}</script>
            </head><body></body></html>
            """);

        var comVitrine = await ProbeAsync("""
            <html><head><title>x</title>
            <script type="application/ld+json">{"@type":"Vehicle","offers":{"@type":"Offer"}}</script>
            </head><body></body></html>
            """);

        // Indistinguíveis, e não deveriam ser.
        Assert.Equal(comOrganization.HasStructuredData, comVitrine.HasStructuredData);
    }
}
