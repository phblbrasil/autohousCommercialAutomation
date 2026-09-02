namespace AutoHous.Revenue.Domain;

/// <summary>
/// Os rastreadores de IA que importam para uma loja de veículos brasileira, e o
/// que cada um faz quando é bloqueado.
///
/// A lista existe porque "bloqueou robô de IA" não é um fato único — bloquear o
/// <c>CCBot</c> tira o site de um dataset de treino; bloquear o
/// <c>OAI-SearchBot</c> tira a loja do resultado que o comprador vê **hoje**,
/// enquanto pergunta onde achar o carro. Tratar os dois como a mesma coisa
/// produziria um diagnóstico que soa alarmante e não diz o que fazer.
///
/// A separação é essa:
///
///   <b>Busca</b>   — responde a pergunta do usuário agora. Bloquear custa
///                    visibilidade imediata, e é quase sempre não intencional.
///   <b>Treino</b>   — alimenta modelo futuro. Bloquear é decisão legítima de
///                    muita empresa, e não deveria contar como defeito.
/// </summary>
public static class AiCrawlers
{
    /// <summary>Rastreador que responde à pergunta do comprador AGORA.</summary>
    public const string PurposeSearch = "search";

    /// <summary>Rastreador que coleta para treino de modelo.</summary>
    public const string PurposeTraining = "training";

    public sealed record Crawler(string UserAgent, string Operator, string Purpose);

    /// <summary>
    /// Nome do agente exatamente como aparece em <c>robots.txt</c>. A comparação
    /// é sem diferenciar maiúsculas, que é como a própria especificação manda.
    /// </summary>
    public static readonly IReadOnlyList<Crawler> All =
    [
        // ------------------------------------------------------------- busca
        new("OAI-SearchBot",  "OpenAI",     PurposeSearch),
        new("ChatGPT-User",   "OpenAI",     PurposeSearch),
        new("PerplexityBot",  "Perplexity", PurposeSearch),
        new("Perplexity-User","Perplexity", PurposeSearch),
        new("Claude-User",    "Anthropic",  PurposeSearch),
        new("Claude-SearchBot","Anthropic", PurposeSearch),
        new("Google-Extended","Google",     PurposeSearch),
        new("Applebot-Extended","Apple",    PurposeSearch),

        // ------------------------------------------------------------ treino
        new("GPTBot",         "OpenAI",     PurposeTraining),
        new("ClaudeBot",      "Anthropic",  PurposeTraining),
        new("CCBot",          "Common Crawl", PurposeTraining),
        new("Bytespider",     "ByteDance",  PurposeTraining),
        new("meta-externalagent", "Meta",   PurposeTraining),
        new("Amazonbot",      "Amazon",     PurposeTraining)
    ];

    public static Crawler? Find(string userAgent) =>
        All.FirstOrDefault(c => string.Equals(c.UserAgent, userAgent, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Dos bloqueados, quantos respondem pergunta de comprador agora.
    ///
    /// É o número que vira argumento comercial. "Você bloqueia 6 robôs de IA" não
    /// move ninguém; "quando alguém pergunta ao ChatGPT onde comprar um carro na
    /// sua cidade, o seu site não pode ser lido" move.
    /// </summary>
    public static int CountSearch(IEnumerable<string>? blocked) =>
        blocked?.Count(ua => Find(ua)?.Purpose == PurposeSearch) ?? 0;
}
