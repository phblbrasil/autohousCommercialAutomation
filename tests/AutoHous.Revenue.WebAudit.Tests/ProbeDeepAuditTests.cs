using System.Net;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.WebAudit;

namespace AutoHous.Revenue.WebAudit.Tests;

/// <summary>
/// As medidas de auditoria profunda: descoberta por motor generativo (GEO),
/// legibilidade por máquina (AEO) e a qualidade que de fato ranqueia.
///
/// Todas cabem na regra do auditor — a sonda **mede**, o agente **observa com
/// evidência**, a plataforma **pontua**. Nada aqui é julgamento: é contagem
/// sobre um documento e leitura de um arquivo de texto.
/// </summary>
public class ProbeDeepAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class MapHandler(
        Dictionary<string, string> rotas,
        Dictionary<string, string>? headers = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (!rotas.TryGetValue(path, out var corpo))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                    Content = new StringContent(string.Empty)
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(corpo, System.Text.Encoding.UTF8, "text/html")
            };

            if (path == "/")
            {
                foreach (var (k, v) in headers ?? []) response.Headers.TryAddWithoutValidation(k, v);
            }

            return Task.FromResult(response);
        }
    }

    private static async Task<WebsiteProbeResult> ProbeAsync(
        string html,
        Dictionary<string, string>? extras = null,
        Dictionary<string, string>? headers = null)
    {
        var rotas = new Dictionary<string, string> { ["/"] = html };
        foreach (var (k, v) in extras ?? []) rotas[k] = v;

        using var handler = new MapHandler(rotas, headers);
        using var client = new HttpClient(handler);

        return await new HttpWebsiteProbe(client, new FixedClock(Now))
            .ProbeAsync("https://exemplo.com.br", TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------ GEO

    /// <summary>
    /// A medida mais acionável da sonda, e a que ninguém olha.
    ///
    /// Uma concessionária que bloqueia os robôs de busca de IA não existe quando
    /// o comprador pergunta ao assistente onde achar o carro — e ninguém no
    /// negócio sabe, porque o bloqueio quase sempre veio de um tutorial de
    /// "proteja seu conteúdo" aplicado sem consequência medida.
    /// </summary>
    [Fact]
    public async Task Bloqueio_de_robo_de_IA_no_robots_e_medido()
    {
        var r = await ProbeAsync("<html><body><p>oi</p></body></html>", new Dictionary<string, string>
        {
            ["/robots.txt"] = "User-agent: GPTBot\nUser-agent: OAI-SearchBot\nDisallow: /\n"
        });

        Assert.NotNull(r.AiCrawlersBlocked);
        Assert.Contains("GPTBot", r.AiCrawlersBlocked);
        Assert.Contains("OAI-SearchBot", r.AiCrawlersBlocked);

        // E o que separa alarme de diagnóstico: um dos dois responde a pergunta
        // do comprador agora.
        Assert.Equal(1, AiCrawlers.CountSearch(r.AiCrawlersBlocked));
    }

    [Fact]
    public async Task Sem_robots_o_bloqueio_e_nulo_e_nao_lista_vazia()
    {
        var r = await ProbeAsync("<html><body><p>oi</p></body></html>");

        // Nulo é "não verificado"; lista vazia seria "verificado, nenhum
        // bloqueado". Sem o arquivo, a segunda afirmação não se sustenta.
        Assert.Null(r.AiCrawlersBlocked);
        Assert.False(r.HasRobotsTxt);
    }

    [Fact]
    public async Task Llms_txt_e_verificado_na_raiz()
    {
        var comArquivo = await ProbeAsync("<html><body>x</body></html>",
            new Dictionary<string, string> { ["/llms.txt"] = "# Loja" });

        var sem = await ProbeAsync("<html><body>x</body></html>");

        Assert.True(comArquivo.HasLlmsTxt);
        Assert.False(sem.HasLlmsTxt);
    }

    /// <summary>
    /// <c>noindex</c> só no cabeçalho é invisível para quem olha o HTML — e é
    /// assim que ele sobrevive a uma migração sem ninguém notar. O sintoma que
    /// chega ao negócio é "as vendas caíram", nunca "o site saiu do índice".
    /// </summary>
    [Fact]
    public async Task Noindex_no_cabecalho_conta_tanto_quanto_na_meta()
    {
        var porHeader = await ProbeAsync("<html><body>x</body></html>", null,
            new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex, nofollow" });

        var porMeta = await ProbeAsync(
            """<html><head><meta name="robots" content="noindex"></head><body>x</body></html>""");

        var normal = await ProbeAsync("<html><body>x</body></html>");

        Assert.False(porHeader.IsIndexable);
        Assert.False(porMeta.IsIndexable);
        Assert.True(normal.IsIndexable);
    }

    /// <summary>
    /// O número que denuncia a vitrine em SPA.
    ///
    /// Um bundle inline de JavaScript não é texto para ninguém. Se ele contasse,
    /// a home mais vazia do funil — a que tem o estoque inteiro atrás de uma
    /// chamada que o rastreador não faz — apareceria como a mais densa.
    /// </summary>
    [Fact]
    public async Task Texto_visivel_ignora_script_e_estilo()
    {
        var spa = await ProbeAsync("""
            <html><body>
              <div id="app"></div>
              <script>var estoque=[{"modelo":"Onix","ano":2022,"preco":78900}];function render(){}</script>
              <style>.card{display:flex;padding:12px;margin:0 auto}</style>
            </body></html>
            """);

        var real = await ProbeAsync("""
            <html><body>
              <h1>Seminovos em Porto Alegre</h1>
              <p>Trinta veiculos revisados com garantia de doze meses e procedencia verificada.</p>
            </body></html>
            """);

        // A home em SPA tem zero palavra para quem não executa JavaScript — e é
        // exatamente isso que o rastreador de IA vê.
        Assert.Equal(0, spa.RawTextWords);

        // 4 palavras no h1 + 11 no parágrafo. O número exato importa: a medida
        // só serve como diagnóstico se contar o que um leitor contaria.
        Assert.Equal(15, real.RawTextWords);
    }

    // ------------------------------------------------------------------ AEO

    /// <summary>
    /// É o que o booleano antigo nunca conseguiu dizer: um rodapé com
    /// <c>Organization</c> e uma vitrine marcada com <c>Vehicle</c> e
    /// <c>Offer</c> eram o mesmo <c>true</c>, e é a segunda que faz o estoque ser
    /// citável por um motor de resposta.
    /// </summary>
    [Fact]
    public async Task Tipos_de_json_ld_sao_extraidos_inclusive_aninhados()
    {
        var r = await ProbeAsync("""
            <html><head>
            <script type="application/ld+json">
            {"@context":"https://schema.org","@graph":[
              {"@type":"AutoDealer","name":"Grupo Vento Sul",
               "telephone":"+555132214400",
               "address":{"@type":"PostalAddress","streetAddress":"Av. Assis Brasil, 3000"}},
              {"@type":["Vehicle","Product"],"name":"Onix 2022",
               "offers":{"@type":"Offer","price":"78900"}}
            ]}
            </script>
            </head><body>x</body></html>
            """);

        Assert.NotNull(r.StructuredDataTypes);
        Assert.Contains("AutoDealer", r.StructuredDataTypes);
        Assert.Contains("Vehicle", r.StructuredDataTypes);
        Assert.Contains("Offer", r.StructuredDataTypes);
        Assert.Contains("PostalAddress", r.StructuredDataTypes);

        // `@type` aceita lista, e a especificacao permite.
        Assert.Contains("Product", r.StructuredDataTypes);

        // Nome, telefone e endereco juntos: o minimo para um motor afirmar QUAL
        // negocio e este.
        Assert.True(r.StructuredDataHasNap);
    }

    [Fact]
    public async Task Json_ld_quebrado_nao_derruba_a_auditoria()
    {
        var r = await ProbeAsync("""
            <html><head>
            <script type="application/ld+json">{"@type": "AutoDealer", isso nao e json}</script>
            <script type="application/ld+json">{"@type":"LocalBusiness"}</script>
            </head><body>x</body></html>
            """);

        // O bloco valido sobrevive ao invalido. Metade dos sites do setor tem um
        // bloco quebrado, e trocar diagnostico parcial por nenhum seria pior.
        Assert.Equal(["LocalBusiness"], r.StructuredDataTypes);
    }

    [Fact]
    public async Task Hierarquia_de_titulos_e_contada()
    {
        var r = await ProbeAsync(
            "<html><body><h1>a</h1><h1>b</h1><h2>c</h2><h2>d</h2><h2>e</h2></body></html>");

        Assert.Equal(2, r.H1Count);
        Assert.Equal(3, r.H2Count);
    }

    // ------------------------------------------------------------ qualidade

    [Fact]
    public async Task Comprimento_de_titulo_e_descricao_sao_medidos()
    {
        var r = await ProbeAsync("""
            <html><head>
              <title>Seminovos</title>
              <meta name="description" content="Doze veiculos.">
            </head><body>x</body></html>
            """);

        Assert.Equal("Seminovos".Length, r.TitleLength);
        Assert.Equal("Doze veiculos.".Length, r.MetaDescriptionLength);
    }

    /// <summary>
    /// Canonical errado é pior que canonical ausente: ausente deixa o buscador
    /// decidir; apontando para outra URL, ele obedece e tira a página do índice.
    /// </summary>
    [Fact]
    public async Task Canonical_apontando_para_outro_lugar_e_detectado()
    {
        var proprio = await ProbeAsync(
            """<html><head><link rel="canonical" href="https://exemplo.com.br/"></head><body>x</body></html>""");

        var alheio = await ProbeAsync(
            """<html><head><link rel="canonical" href="https://outrodominio.com.br/"></head><body>x</body></html>""");

        var ausente = await ProbeAsync("<html><head></head><body>x</body></html>");

        Assert.True(proprio.CanonicalIsSelfReferencing);
        Assert.False(alheio.CanonicalIsSelfReferencing);
        Assert.Null(ausente.CanonicalIsSelfReferencing);
    }

    /// <summary>
    /// Numa vitrine, imagem é a maior parte do peso — e peso é tempo de
    /// carregamento, que vira custo de mídia. Dimensão declarada é o proxy de CLS
    /// que dá para medir sem navegador: sem ela o layout pula em cada card.
    /// </summary>
    [Fact]
    public async Task Imagens_sao_medidas_por_alt_dimensao_e_formato()
    {
        var r = await ProbeAsync("""
            <html><body>
              <img src="/onix.webp" alt="Onix 2022" width="400" height="300">
              <img src="/hb20.jpg" alt="HB20 2021">
              <img src="/banner.png">
              <img src="/logo.avif" alt="Logo" width="120" height="40">
            </body></html>
            """);

        Assert.Equal(4, r.ImageCount);
        Assert.Equal(3, r.ImagesWithAlt);
        Assert.Equal(2, r.ImagesWithDimensions);
        Assert.Equal(2, r.ImagesModernFormat);
    }

    [Fact]
    public async Task Links_internos_separam_navegacao_de_saida()
    {
        var r = await ProbeAsync("""
            <html><body>
              <a href="/estoque">Estoque</a>
              <a href="/financiamento">Financiamento</a>
              <a href="https://exemplo.com.br/contato">Contato</a>
              <a href="https://webmotors.com.br/loja">Webmotors</a>
              <a href="mailto:oi@exemplo.com.br">E-mail</a>
              <a href="tel:+555132214400">Telefone</a>
            </body></html>
            """);

        // Três internos. Marketplace externo, mailto e tel não são navegação
        // para um rastreador.
        Assert.Equal(3, r.InternalLinkCount);
    }

    [Fact]
    public async Task Hsts_e_idioma_declarado_sao_lidos()
    {
        var r = await ProbeAsync(
            """<html lang="pt-BR"><body>x</body></html>""", null,
            new Dictionary<string, string> { ["Strict-Transport-Security"] = "max-age=31536000" });

        Assert.True(r.HasHsts);
        Assert.Equal("pt-BR", r.DeclaredLanguage);
    }
}
