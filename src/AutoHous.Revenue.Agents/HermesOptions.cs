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
    /// Transporte.
    ///
    /// O padrao era <see cref="HermesTransport.Runs"/>, com "chat" documentado
    /// como contingencia caso o envelope de /v1/runs divergisse. Ao instalar o
    /// Hermes v0.21.0 a divergencia deixou de ser hipotese, e ela e fatal:
    ///
    /// <c>GET /v1/runs/{id}</c> devolve o dicionario de <c>_run_statuses</c> tal
    /// como <c>_set_run_status</c> o montou, e NENHUM call site passa
    /// <c>output</c>. O texto final so existe no evento <c>assistant.completed</c>
    /// da SSE, e essa fila nao guarda historico: quem faz polling ate ver
    /// <c>status: completed</c> chega depois de o texto ter passado.
    ///
    /// O sintoma seria o pior possivel - <c>RawText</c> vazio em 100% dos runs,
    /// reprovado pelo validador como <c>contract_violation</c>. A leitura obvia
    /// disso e "o modelo nao consegue formatar JSON", e nao "o cliente le o campo
    /// errado", de modo que a investigacao comecaria pelo prompt e nao pelo
    /// transporte.
    ///
    /// <see cref="HermesTransport.Chat"/> usa POST /v1/chat/completions, cujo
    /// envelope e o da OpenAI - <c>choices[0].message.content</c> e
    /// <c>usage.prompt_tokens</c> - conferido na fonte instalada. E tambem o
    /// unico dos dois que le o header X-Hermes-Session-Id, entao a correlacao com
    /// research_run_id passa a funcionar em vez de falhar em silencio.
    ///
    /// Runs continua disponivel: quando o gateway voltar a expor o texto no
    /// status, ele e o transporte melhor para sessao longa.
    /// </summary>
    public HermesTransport Transport { get; set; } = HermesTransport.Chat;

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
