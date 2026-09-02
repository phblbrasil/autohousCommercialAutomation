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

        var root = await ProbeRootFilesAsync(finalUri, ct);

        var doc = Parser.ParseDocument(html);
        var viewport = Attr(doc, "meta[name=viewport]", "content");
        var title = doc.QuerySelector("head > title")?.TextContent?.Trim();
        var description = Attr(doc, "meta[name=description]", "content")?.Trim();
        var canonical = Attr(doc, "link[rel=canonical]", "href");
        var images = doc.QuerySelectorAll("img").OfType<IHtmlImageElement>().ToList();
        var jsonLd = JsonLd.Read(doc);

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
            HasTitle = !string.IsNullOrWhiteSpace(title),
            HasMetaDescription = !string.IsNullOrWhiteSpace(description),
            HasH1 = doc.QuerySelector("h1") is not null,
            HasCanonical = canonical is not null,
            HasStructuredData = jsonLd.Types.Count > 0
                                || doc.QuerySelectorAll("[itemtype*='schema.org']").Length > 0,
            HasSitemap = root.Sitemap,
            HasRobotsTxt = root.Robots is not null,

            HasViewportMeta = viewport is not null,
            HasFixedWidthViewport = viewport is not null && FixedWidth(viewport),

            // ---------------------------------------------------------- GEO
            AiCrawlersBlocked = root.Robots is { } robotsTxt
                ? RobotsTxt.BlockedAiCrawlers(robotsTxt)
                : null,
            HasLlmsTxt = root.LlmsTxt,
            IsIndexable = IsIndexable(doc, response),
            RawTextWords = CountWords(doc),

            // ---------------------------------------------------------- AEO
            StructuredDataTypes = jsonLd.Types,
            StructuredDataHasNap = jsonLd.HasNap,
            H1Count = doc.QuerySelectorAll("h1").Length,
            H2Count = doc.QuerySelectorAll("h2").Length,

            // ---------------------------------------------------- qualidade
            TitleLength = title?.Length,
            MetaDescriptionLength = description?.Length,
            CanonicalIsSelfReferencing = canonical is null
                ? null
                : SameResource(canonical, finalUri),
            ImageCount = images.Count,
            ImagesWithAlt = images.Count(i => !string.IsNullOrWhiteSpace(i.AlternativeText)),
            ImagesWithDimensions = images.Count(i =>
                i.GetAttribute("width") is not null && i.GetAttribute("height") is not null),
            ImagesModernFormat = images.Count(i => IsModernFormat(i.GetAttribute("src"))),
            HasHsts = response.Headers.Contains("Strict-Transport-Security"),
            InternalLinkCount = CountInternalLinks(doc, finalUri),
            DeclaredLanguage = doc.QuerySelector("html")?.GetAttribute("lang"),

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

    private sealed record RootFiles(string? Robots, bool? Sitemap, bool? LlmsTxt);

    /// <summary>
    /// Os tres arquivos de raiz. Falha nao vira nulo por acaso: as requisicoes
    /// sao independentes do documento, e um erro aqui significa "nao consegui
    /// verificar", que e o que o nulo diz.
    ///
    /// O <c>robots.txt</c> agora e BAIXADO, e nao so verificado por HEAD - e
    /// dentro dele que esta a medida mais acionavel da sonda: quais rastreadores
    /// de IA a loja bloqueia sem saber.
    /// </summary>
    private async Task<RootFiles> ProbeRootFilesAsync(Uri baseUri, CancellationToken ct)
    {
        var root = new Uri(baseUri, "/");

        var robots = await FetchAsync(new Uri(root, "robots.txt"), ct);
        var sitemap = await ExistsAsync(new Uri(root, "sitemap.xml"), ct);
        var llms = await ExistsAsync(new Uri(root, "llms.txt"), ct);

        return new RootFiles(robots, sitemap, llms);
    }

    /// <summary>
    /// Baixa um arquivo de texto pequeno da raiz. Devolve nulo quando nao existe
    /// ou nao deu para buscar - a diferenca entre os dois nao muda o
    /// diagnostico, porque nos dois casos nao ha regra a ler.
    /// </summary>
    private async Task<string?> FetchAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(uri, ct);

            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync(ct);

            // robots.txt legitimo nao passa de alguns KB. Arquivo gigante aqui e
            // pagina de erro servida com 200, que e comum e nao e robots.
            return text.Length > 64 * 1024 ? null : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
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
    /// O documento se declara indexavel?
    ///
    /// Olha os dois lugares onde a diretiva vive - a meta robots e o cabecalho
    /// <c>X-Robots-Tag</c> - porque um <c>noindex</c> so no cabecalho e
    /// invisivel para quem olha o HTML, e e assim que ele sobrevive a uma
    /// migracao sem ninguem notar.
    /// </summary>
    private static bool IsIndexable(IDocument doc, HttpResponseMessage response)
    {
        var meta = doc.QuerySelector("meta[name=robots]")?.GetAttribute("content") ?? string.Empty;

        var header = response.Headers.TryGetValues("X-Robots-Tag", out var values)
            ? string.Join(",", values)
            : string.Empty;

        return !meta.Contains("noindex", StringComparison.OrdinalIgnoreCase)
               && !header.Contains("noindex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Palavras de texto visivel, sem executar JavaScript.
    ///
    /// <c>script</c>, <c>style</c>, <c>noscript</c> e <c>template</c> saem antes
    /// da contagem: o conteudo deles nao e texto para ninguem, e um bundle
    /// inline de 300 KB faria uma home vazia parecer densa - invertendo
    /// exatamente o diagnostico que esta medida existe para dar.
    /// </summary>
    private static int CountWords(IDocument doc)
    {
        var body = doc.Body;

        if (body is null) return 0;

        var clone = (IElement)body.Clone(true);

        foreach (var noise in clone.QuerySelectorAll("script, style, noscript, template").ToList())
        {
            noise.Remove();
        }

        return clone.TextContent
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(w => w.Any(char.IsLetterOrDigit));
    }

    /// <summary>
    /// Links que ficam no proprio dominio. Ancora, <c>mailto:</c>, <c>tel:</c> e
    /// <c>javascript:</c> nao sao navegacao para um rastreador e ficam de fora.
    /// </summary>
    private static int CountInternalLinks(IDocument doc, Uri baseUri) =>
        doc.QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href"))
            .Count(href => href is not null
                           && Uri.TryCreate(baseUri, href, out var abs)
                           && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
                           && string.Equals(abs.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// O canonical aponta para a propria pagina? Compara host e caminho e ignora
    /// query e fragmento, que e o que a maioria dos canonicais faz de proposito.
    /// </summary>
    private static bool SameResource(string canonical, Uri current) =>
        Uri.TryCreate(current, canonical, out var abs)
        && string.Equals(abs.Host, current.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(abs.AbsolutePath.TrimEnd('/'), current.AbsolutePath.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsModernFormat(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;

        var path = src.Split('?')[0].Split('#')[0];

        return path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase);
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
