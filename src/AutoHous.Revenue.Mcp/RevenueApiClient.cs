using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AutoHous.Revenue.Mcp;

/// <summary>
/// Unico caminho de dados do MCP: HTTP contra a Revenue API.
///
/// ADR-003: o Hermes nunca recebe service_role nem string de conexao. Este
/// projeto nao referencia Npgsql - a fronteira e estrutural, nao apenas
/// convencionada.
/// </summary>
public sealed class RevenueApiClient
{
    private readonly HttpClient _http;

    public RevenueApiClient(HttpClient http, RevenueApiOptions options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.BaseUrl);

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public async Task<string> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return JsonSerializer.Serialize(new { error = "not_found", path });
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}

public sealed class RevenueApiOptions
{
    public string BaseUrl { get; set; } =
        Environment.GetEnvironmentVariable("REVENUE_API_URL") ?? "http://127.0.0.1:5080";

    /// <summary>
    /// Credencial de borda da Revenue API.
    ///
    /// <c>REVENUE_API_KEY_FILE</c> vem primeiro porque e o formato que Docker
    /// secrets e Kubernetes montam - e porque, no caminho do Hermes, a
    /// alternativa seria a chave literal dentro de <c>~/.hermes/config.yaml</c>:
    /// o filtro de ambiente do Hermes so repassa ao subprocesso do MCP o que
    /// esta declarado no bloco <c>env:</c> do servidor, sem interpolar
    /// <c>${VAR}</c>. Caminho de arquivo no config, segredo no arquivo.
    /// </summary>
    public string ApiKey { get; set; } = ResolveApiKey();

    private static string ResolveApiKey()
    {
        var path = Environment.GetEnvironmentVariable("REVENUE_API_KEY_FILE");

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return File.ReadAllText(path).Trim();
        }

        return Environment.GetEnvironmentVariable("REVENUE_API_KEY") ?? string.Empty;
    }
}
