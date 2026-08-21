using AutoHous.Revenue.Application;
namespace AutoHous.Revenue.Agents;

public sealed class HermesOptions
{
    public const string SectionName = "Hermes";

    /// <summary>O API Server escuta 127.0.0.1:8642 por padrao.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8642";

    /// <summary>
    /// Valor de API_SERVER_KEY. O servidor exige Bearer em toda requisicao.
    /// Nunca versionar: vem de variavel de ambiente (secao 26).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Nome anunciado pelo servidor; o padrao do Hermes e "hermes-agent".</summary>
    public string Model { get; set; } = "hermes-agent";

    /// <summary>
    /// Transporte. "runs" usa POST /v1/runs (desenhado para sessoes longas, que e
    /// o caso de uma pesquisa com navegacao web). "chat" usa
    /// POST /v1/chat/completions, cujo envelope e o da OpenAI e portanto conhecido
    /// com exatidao - util como caminho de contingencia se o envelope de /v1/runs
    /// divergir do esperado ao ativar o Hermes real.
    /// </summary>
    public HermesTransport Transport { get; set; } = HermesTransport.Runs;

    /// <summary>Teto de espera por um run de pesquisa.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Tentativas em falhas transitorias (429 do cap de concorrencia, 5xx).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base do backoff exponencial entre tentativas.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
}

public enum HermesTransport
{
    Runs,
    Chat
}
