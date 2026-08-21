using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AutoHous.Revenue.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>
/// O repositorio de Dados Abertos CNPJ, como ele existe hoje: um Nextcloud
/// (SERPRO+) exposto por um compartilhamento publico.
///
/// Os caminhos que circulam na internet - <c>/dados/cnpj/dados_abertos_cnpj/</c>
/// e o antigo <c>dadosabertos.rfb.gov.br</c> - respondem 404 e timeout. O que
/// funciona e WebDAV sobre o compartilhamento, autenticado por Basic com o token
/// no lugar do usuario e senha vazia.
///
/// Duas escolhas que parecem detalhe e nao sao:
///
/// - **O token e descoberto**, do redirect da raiz, em vez de embutido. Ele ja
///   mudou uma vez junto com a plataforma; fixado no codigo, a proxima mudanca
///   derruba a carga mensal sem aviso.
/// - **<c>Range</c> e obrigatorio.** <c>Estabelecimentos0.zip</c> tem 2 GB. Sem
///   retomada, qualquer queda de conexao reinicia o download do zero.
/// </summary>
public sealed partial class ReceitaFederalArchive(
    HttpClient http,
    IOptions<ReceitaOptions> options,
    ILogger<ReceitaFederalArchive> logger) : IReceitaFederalArchive
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly HttpMethod Propfind = new("PROPFIND");

    private readonly ReceitaOptions _options = options.Value;
    private string? _token;

    [GeneratedRegex(@"^\d{4}-\d{2}$")]
    private static partial Regex ReleasePattern { get; }

    public async Task<IReadOnlyList<string>> ListReleasesAsync(CancellationToken ct = default)
    {
        var entries = await PropfindAsync(_options.BasePath, ct);

        return
        [
            .. entries
                .Where(e => e.IsDirectory && ReleasePattern.IsMatch(e.Name))
                .Select(e => e.Name)
                .Order(StringComparer.Ordinal)
        ];
    }

    public async Task<IReadOnlyList<ReceitaArchiveFile>> ListFilesAsync(
        string release, CancellationToken ct = default)
    {
        var entries = await PropfindAsync($"{_options.BasePath}/{release}", ct);

        return
        [
            .. entries
                .Where(e => !e.IsDirectory)
                .Select(e => new ReceitaArchiveFile(e.Name, e.Length, e.LastModified))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
        ];
    }

    public async Task<Stream> OpenAsync(
        string release, string fileName, long offset, CancellationToken ct = default)
    {
        var request = await BuildAsync(HttpMethod.Get, $"{_options.BasePath}/{release}/{fileName}", ct);

        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // Um servidor que ignora Range devolve 200 com o arquivo inteiro. Tratar
        // isso como retomada escreveria o arquivo de novo a partir do offset e
        // produziria um zip corrompido de tamanho plausivel.
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();

            throw new InvalidOperationException(
                $"A origem ignorou Range em '{fileName}' (respondeu {(int)response.StatusCode}). " +
                "Retomada impossivel: apague o arquivo parcial no cache e baixe de novo.");
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(ct);
    }

    // ------------------------------------------------------------------ WebDAV

    private sealed record DavEntry(string Name, bool IsDirectory, long Length, DateTimeOffset LastModified);

    private async Task<IReadOnlyList<DavEntry>> PropfindAsync(string path, CancellationToken ct)
    {
        using var request = await BuildAsync(Propfind, path, ct);
        request.Headers.Add("Depth", "1");

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Caminho inexistente no repositorio da Receita: '{path}'. " +
                "Confira o release com --list.");
        }

        response.EnsureSuccessStatusCode();

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var basePath = $"/{path.Trim('/')}";
        var entries = new List<DavEntry>();

        foreach (var element in xml.Descendants(Dav + "response"))
        {
            var href = element.Element(Dav + "href")?.Value;
            if (href is null) continue;

            var decoded = Uri.UnescapeDataString(href).TrimEnd('/');
            var name = decoded[(decoded.LastIndexOf('/') + 1)..];

            // A propria pasta consultada vem no resultado do PROPFIND. Sem esta
            // linha, "2026-08" apareceria como filho de si mesmo.
            if (name.Length == 0 || decoded.EndsWith(basePath, StringComparison.Ordinal)) continue;

            var prop = element.Descendants(Dav + "prop").FirstOrDefault();
            var lengthText = prop?.Element(Dav + "getcontentlength")?.Value;
            var modifiedText = prop?.Element(Dav + "getlastmodified")?.Value;

            entries.Add(new DavEntry(
                name,
                // Pasta nao declara getcontentlength. E o unico marcador que o
                // Nextcloud oferece de forma confiavel nesta resposta.
                IsDirectory: lengthText is null,
                Length: long.TryParse(lengthText, out var length) ? length : 0,
                LastModified: DateTimeOffset.TryParse(
                    modifiedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified)
                    ? modified
                    : default));
        }

        return entries;
    }

    private async Task<HttpRequestMessage> BuildAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var token = await ResolveTokenAsync(ct);

        var request = new HttpRequestMessage(
            method, new Uri($"{_options.BaseUrl.TrimEnd('/')}/public.php/webdav/{path.TrimStart('/')}"));

        // Compartilhamento publico do Nextcloud: o token entra como usuario e a
        // senha e vazia.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{token}:")));

        return request;
    }

    /// <summary>
    /// Descobre o token do compartilhamento seguindo o redirect da raiz do site,
    /// que aponta para <c>/index.php/s/&lt;token&gt;</c>.
    /// </summary>
    private async Task<string> ResolveTokenAsync(CancellationToken ct)
    {
        if (_token is not null) return _token;

        if (!string.IsNullOrWhiteSpace(_options.ShareToken))
        {
            return _token = _options.ShareToken.Trim();
        }

        using var response = await http.GetAsync(_options.BaseUrl, ct);
        var landed = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;

        var marker = landed.IndexOf("/s/", StringComparison.Ordinal);

        if (marker < 0)
        {
            throw new InvalidOperationException(
                $"Nao foi possivel descobrir o token do compartilhamento da Receita em '{_options.BaseUrl}' " +
                $"(chegou em '{landed}'). Defina {ReceitaOptions.EnvShareToken} no ambiente.");
        }

        var token = landed[(marker + 3)..].Trim('/');

        logger.LogInformation("Token do compartilhamento da Receita descoberto: {Token}", token);

        return _token = token;
    }
}
