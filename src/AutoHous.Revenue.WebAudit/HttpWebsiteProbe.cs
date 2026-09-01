using System.Diagnostics;
using System.Text.RegularExpressions;
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
public sealed partial class HttpWebsiteProbe(
    HttpClient http,
    IClock clock,
    ILogger<HttpWebsiteProbe>? logger = null) : IWebsiteProbe
{
    public string Name => "http-probe-v1";

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
        var head = HeadOf(html);

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

        return new WebsiteProbeResult
        {
            RequestedUrl = url,
            FinalUrl = finalUri.ToString(),
            StatusCode = (int)response.StatusCode,

            TimeToFirstByte = ttfb,
            DocumentLoadTime = loadTime,
            DocumentBytes = bytes,
            RenderBlockingResources = CountRenderBlocking(head),
            CompressionEnabled = response.Content.Headers.ContentEncoding.Count > 0
                                 || response.Headers.Vary.Contains("Accept-Encoding"),

            IsHttps = finalUri.Scheme == Uri.UriSchemeHttps,
            HasTitle = TitleRegex().IsMatch(head),
            HasMetaDescription = MetaDescriptionRegex().IsMatch(head),
            HasH1 = H1Regex().IsMatch(html),
            HasCanonical = CanonicalRegex().IsMatch(head),
            HasStructuredData = StructuredDataRegex().IsMatch(html),
            HasSitemap = sitemap,
            HasRobotsTxt = robots,

            HasViewportMeta = ViewportRegex().IsMatch(head),
            HasFixedWidthViewport = FixedWidthViewportRegex().IsMatch(head),

            Technologies = TechnologySignatures.DetectAll(html),

            ObservedAt = observedAt
        };
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
    /// Só o <c>&lt;head&gt;</c>. As regex de SEO e viewport nao devem casar com
    /// um &lt;title&gt; dentro de um SVG no corpo da pagina.
    /// </summary>
    private static string HeadOf(string html)
    {
        var end = html.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
        return end > 0 ? html[..end] : html;
    }

    /// <summary>
    /// Script sincrono e folha de estilo no head bloqueiam a primeira pintura.
    /// Script com async ou defer nao bloqueia, e por isso e descontado.
    /// </summary>
    private static int CountRenderBlocking(string head)
    {
        var scripts = ScriptSrcRegex().Matches(head).Count(m =>
            !m.Value.Contains("async", StringComparison.OrdinalIgnoreCase) &&
            !m.Value.Contains("defer", StringComparison.OrdinalIgnoreCase));

        return scripts + StylesheetRegex().Matches(head).Count;
    }

    [GeneratedRegex(@"<title[^>]*>\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta[^>]+name\s*=\s*[""']description[""'][^>]+content\s*=\s*[""']\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex MetaDescriptionRegex();

    [GeneratedRegex(@"<h1[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex H1Regex();

    [GeneratedRegex(@"<link[^>]+rel\s*=\s*[""']canonical[""']", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalRegex();

    [GeneratedRegex(@"application/ld\+json|itemtype\s*=\s*[""']https?://schema\.org", RegexOptions.IgnoreCase)]
    private static partial Regex StructuredDataRegex();

    [GeneratedRegex(@"<meta[^>]+name\s*=\s*[""']viewport[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ViewportRegex();

    [GeneratedRegex(@"<meta[^>]+name\s*=\s*[""']viewport[""'][^>]+content\s*=\s*[""'][^""']*width\s*=\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex FixedWidthViewportRegex();

    [GeneratedRegex(@"<script[^>]+src\s*=[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrcRegex();

    [GeneratedRegex(@"<link[^>]+rel\s*=\s*[""']stylesheet[""']", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetRegex();
}
