using System.Diagnostics;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.WebAudit;

/// <summary>
/// Sonda de site sobre HTTP puro. Primeira implementacao de
/// <see cref="IWebsiteProbe"/>.
///
/// **O que ela mede de verdade:** tempo ate o primeiro byte, tempo do documento,
/// peso do HTML, compressao, recursos que bloqueiam a renderizacao, os sinais de
/// SEO que vivem no <c>&lt;head&gt;</c>, o viewport, e as assinaturas de
/// tecnologia presentes no HTML entregue.
///
/// **O que ela nao ve, e um browser veria:** tudo que so existe depois do
/// JavaScript rodar. Numa vitrine em SPA - comum no setor - isso e o estoque
/// inteiro. Por isso a contagem de veiculos e pergunta para o AGENTE, que navega
/// de verdade pelo backend de browser do Hermes, e nunca para esta classe.
///
/// Toda medicao anulavel volta nula quando nao foi possivel obte-la, e nunca
/// zero ou false: <see cref="WebsiteAuditScoring"/> trata "nao observado" como
/// dimensao ausente, e um `false` inventado aqui viraria dor no Technology Pain
/// de uma conta que talvez nao a tenha.
/// </summary>
public sealed class HttpWebsiteProbe(
    HttpClient http,
    IClock clock,
    ILogger<HttpWebsiteProbe>? logger = null) : IWebsiteProbe
{
    public string Name => "http-probe-v2";

    /// <summary>
    /// O documento e lido com um parser de HTML de verdade, e nao com regex.
    ///
    /// A escolha anterior nao era descuido: enquanto a sonda so respondia
    /// "existe title?", "existe viewport?", regex bastava. O que fez a conta
    /// virar foi o tipo de pergunta - contar imagem COM atributo, extrair texto
    /// visivel sem script nem estilo, separar link interno de externo. Nenhuma
    /// delas se resolve com casamento de padrao sobre texto.
    ///
    /// E o motor antigo ja errava em silencio no que media: `&lt;h1&gt;` dentro
    /// de comentario HTML contava como titulo, e a palavra
    /// `application/ld+json` escrita num comentario marcava a pagina como tendo
    /// dado estruturado. Comentario nao e elemento; para um parser isso nem e
    /// caso especial.
    ///
    /// O custo e um parse de ~10-30 ms por pagina contra um TTFB de centenas de
    /// milissegundos. E ruido ao lado do I/O.
    /// </summary>
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Teto de leitura do documento. Uma home de concessionaria com 2 MB de HTML
    /// ja e um achado por si so; passar disso e so gastar memoria para medir o
    /// mesmo fato.
    /// </summary>
    private const int MaxDocumentBytes = 4 * 1024 * 1024;

    public async Task<WebsiteProbeResult> ProbeAsync(string url, CancellationToken ct = default)
    {
        var observedAt = clock.UtcNow;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return WebsiteProbeResult.Unreachable(url, $"URL invalida: '{url}'.", observedAt);
        }

        try
        {
            return await MeasureAsync(url, uri, observedAt, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout do proprio HttpClient. Site que nao responde a tempo e um
            // RESULTADO da auditoria - e forte -, nao um erro de execucao.
            return WebsiteProbeResult.Unreachable(url, "Tempo esgotado ao acessar o site.", observedAt);
        }
        catch (HttpRequestException ex)
        {
            return WebsiteProbeResult.Unreachable(url, $"Falha de rede: {ex.Message}", observedAt);
        }
    }

    private async Task<WebsiteProbeResult> MeasureAsync(
        string url, Uri uri, DateTimeOffset observedAt, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // ResponseHeadersRead e o que torna o TTFB uma medida e nao um numero:
        // com o default (ResponseContentRead) o await so retorna com o corpo
        // inteiro em memoria, e "tempo ate o primeiro byte" passaria a medir o
        // download completo.
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        var ttfb = stopwatch.Elapsed;

        var (html, bytes) = await ReadBoundedAsync(response, ct);
        var loadTime = stopwatch.Elapsed;

        logger?.LogDebug(
            "Sonda em {Url}: {Status}, TTFB {Ttfb}ms, {Bytes} bytes",
            url, (int)response.StatusCode, ttfb.TotalMilliseconds, bytes);

        var finalUri = response.RequestMessage?.RequestUri ?? uri;

        // Erro do servidor: ha status, e ele ja e a resposta. Nao vale gastar
        // duas requisicoes extras (robots, sitemap) sobre um site quebrado.
        if (!response.IsSuccessStatusCode)
        {
            return new WebsiteProbeResult
            {
                RequestedUrl = url,
                FinalUrl = finalUri.ToString(),
                StatusCode = (int)response.StatusCode,
                Error = $"O site respondeu {(int)response.StatusCode}.",
                TimeToFirstByte = ttfb,
                DocumentLoadTime = loadTime,
                DocumentBytes = bytes,
                ObservedAt = observedAt
            };
        }

        var (robots, sitemap) = await ProbeRootFilesAsync(finalUri, ct);

        var doc = Parser.ParseDocument(html);
        var viewport = Attr(doc, "meta[name=viewport]", "content");

        return new WebsiteProbeResult
        {
            RequestedUrl = url,
            FinalUrl = finalUri.ToString(),
            StatusCode = (int)response.StatusCode,

            TimeToFirstByte = ttfb,
            DocumentLoadTime = loadTime,
            DocumentBytes = bytes,
            RenderBlockingResources = CountRenderBlocking(doc),
            CompressionEnabled = response.Content.Headers.ContentEncoding.Count > 0
                                 || response.Headers.Vary.Contains("Accept-Encoding"),

            IsHttps = finalUri.Scheme == Uri.UriSchemeHttps,

            // `head > title` e nao `doc.Title`: o segundo acha um <title> de SVG
            // no corpo da pagina, que nao e titulo de documento nenhum.
            HasTitle = !string.IsNullOrWhiteSpace(doc.QuerySelector("head > title")?.TextContent),
            HasMetaDescription = !string.IsNullOrWhiteSpace(Attr(doc, "meta[name=description]", "content")),
            HasH1 = doc.QuerySelector("h1") is not null,
            HasCanonical = doc.QuerySelector("link[rel=canonical]") is not null,
            HasStructuredData = doc.QuerySelectorAll("script[type='application/ld+json']").Length > 0
                                || doc.QuerySelectorAll("[itemtype*='schema.org']").Length > 0,
            HasSitemap = sitemap,
            HasRobotsTxt = robots,

            HasViewportMeta = viewport is not null,
            HasFixedWidthViewport = viewport is not null && FixedWidth(viewport),

            // Assinatura de tecnologia continua sobre o HTML CRU, de proposito:
            // o que ela procura sao trechos literais - `gtag/js?id=G-`,
            // `wa.me/` - que aparecem dentro de atributos e de corpo de script.
            // Passar pelo DOM esconderia justamente onde eles vivem.
            Technologies = TechnologySignatures.DetectAll(html),

            ObservedAt = observedAt
        };
    }

    private static string? Attr(IDocument doc, string selector, string attribute) =>
        doc.QuerySelector(selector)?.GetAttribute(attribute);

    /// <summary>
    /// Largura fixa em px no viewport — o oposto de responsivo.
    /// <c>width=device-width</c> passa; <c>width=1024</c> não.
    /// </summary>
    private static bool FixedWidth(string viewport)
    {
        var width = viewport
            .Split(',')
            .Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("width=", StringComparison.OrdinalIgnoreCase));

        return width is not null && char.IsAsciiDigit(width["width=".Length..].Trim().FirstOrDefault());
    }

    /// <summary>
    /// Le no maximo <see cref="MaxDocumentBytes"/>. Um site que devolve um stream
    /// infinito - acontece com pagina de erro mal feita - nao pode derrubar o
    /// worker por memoria.
    /// </summary>
    private static async Task<(string Html, long Bytes)> ReadBoundedAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length >= MaxDocumentBytes) break;
        }

        // Muito site automotivo brasileiro ainda serve ISO-8859-1 sem declarar.
        // UTF8 com replacement char nao quebra a leitura, e as assinaturas de
        // tecnologia sao ASCII - basta para o que a sonda precisa.
        var text = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

        return (text, buffer.Length);
    }

    /// <summary>
    /// robots.txt e sitemap.xml. Falha nao vira nulo por acaso: as duas
    /// requisicoes sao independentes do documento, e um erro aqui significa "nao
    /// consegui verificar", que e o que o nulo diz.
    /// </summary>
    private async Task<(bool? Robots, bool? Sitemap)> ProbeRootFilesAsync(Uri baseUri, CancellationToken ct)
    {
        var root = new Uri(baseUri, "/");

        var robots = await ExistsAsync(new Uri(root, "robots.txt"), ct);
        var sitemap = await ExistsAsync(new Uri(root, "sitemap.xml"), ct);

        return (robots, sitemap);
    }

    private async Task<bool?> ExistsAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            // Muito servidor recusa HEAD com 405 e serve o mesmo caminho no GET.
            // Tratar 405 como "nao existe" reprovaria o SEO de sites que tem o
            // arquivo - por isso a segunda tentativa.
            if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                using var getResponse = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
                return getResponse.IsSuccessStatusCode;
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Script sincrono e folha de estilo bloqueiam a primeira pintura. Script
    /// com <c>async</c> ou <c>defer</c> nao bloqueia, e por isso e descontado.
    ///
    /// Escopo no <c>&lt;head&gt;</c>, e o parser e quem decide o que esta nele:
    /// pelas regras de parsing do HTML5, uma folha de estilo solta no meio do
    /// corpo NAO vai para o head. A contagem por texto nao sabia disso.
    /// </summary>
    private static int CountRenderBlocking(IDocument doc)
    {
        var head = doc.Head;

        if (head is null) return 0;

        var scripts = head.QuerySelectorAll("script[src]")
            .OfType<IHtmlScriptElement>()
            .Count(s => !s.IsAsync && !s.IsDeferred);

        return scripts + head.QuerySelectorAll("link[rel=stylesheet]").Length;
    }
}
