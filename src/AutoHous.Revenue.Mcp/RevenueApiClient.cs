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

    public string ApiKey { get; set; } =
        Environment.GetEnvironmentVariable("REVENUE_API_KEY") ?? string.Empty;
}
