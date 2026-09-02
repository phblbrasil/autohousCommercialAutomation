namespace AutoHous.Revenue.Domain;

/// <summary>
/// O que a plataforma MEDIU no site, antes de qualquer interpretacao.
///
/// Existe separado de <see cref="Contracts.WebsiteAuditProfile"/> por uma razao
/// que e a regra central do sistema, e nao arrumacao: um modelo de linguagem nao
/// observa tempo de resposta nem peso de pagina - ele estima, e uma estimativa
/// travestida de medicao entra em <c>website_audits.performance_score</c>, vira
/// Technology Pain e sai numa abordagem comercial como se fosse fato.
///
/// A divisao do auditor e portanto:
///
///     sonda  ->  mede    (este record)      deterministico, reproduzivel
///     agente ->  observa (WebsiteAuditProfile) com evidencia, sob a Regra 1
///     plataforma -> pontua (WebsiteAuditScoring)  aritmetica sobre os dois
///
/// Todo campo e anulavel porque "nao consegui medir" e diferente de "medi zero".
/// O score reporta a dimensao como nao observada em vez de assumir o pior - a
/// mesma distincao que <see cref="ScoreComponent.Observed"/> faz no Opportunity
/// Score.
/// </summary>
public sealed record WebsiteProbeResult
{
    /// <summary>URL pedida, antes de redirects.</summary>
    public required string RequestedUrl { get; init; }

    /// <summary>URL onde a sonda parou. Difere da pedida em redirect de dominio.</summary>
    public string? FinalUrl { get; init; }

    /// <summary>Nulo quando o site nao respondeu. Ver <see cref="Error"/>.</summary>
    public int? StatusCode { get; init; }

    public string? Error { get; init; }

    /// <summary>Sonda que alcancou o site.</summary>
    public bool Reached => StatusCode is >= 200 and < 400;

    // ------------------------------------------------------------ desempenho

    /// <summary>Tempo ate o primeiro byte do documento.</summary>
    public TimeSpan? TimeToFirstByte { get; init; }

    /// <summary>Tempo ate o documento HTML terminar de chegar.</summary>
    public TimeSpan? DocumentLoadTime { get; init; }

    /// <summary>Bytes do HTML como veio da rede (comprimido, se houver).</summary>
    public long? DocumentBytes { get; init; }

    /// <summary>
    /// Scripts e folhas de estilo que bloqueiam a primeira renderizacao: script
    /// sincrono no head e link rel=stylesheet sem media condicional.
    /// </summary>
    public int? RenderBlockingResources { get; init; }

    /// <summary>Servidor devolveu gzip/br/deflate.</summary>
    public bool? CompressionEnabled { get; init; }

    // ------------------------------------------------------------------- SEO

    public bool? IsHttps { get; init; }
    public bool? HasTitle { get; init; }
    public bool? HasMetaDescription { get; init; }
    public bool? HasH1 { get; init; }
    public bool? HasCanonical { get; init; }
    public bool? HasStructuredData { get; init; }
    public bool? HasSitemap { get; init; }
    public bool? HasRobotsTxt { get; init; }

    // ---------------------------------------------------------------- mobile

    public bool? HasViewportMeta { get; init; }

    /// <summary>Largura fixa em px no viewport - o oposto de responsivo.</summary>
    public bool? HasFixedWidthViewport { get; init; }

    // --------------------------------- descoberta por motor generativo (GEO)

    /// <summary>
    /// Rastreadores de IA que o <c>robots.txt</c> BLOQUEIA.
    ///
    /// E a medida mais acionavel desta sonda, e a que ninguem olha. Uma
    /// concessionaria que bloqueia <c>GPTBot</c> nao existe quando o comprador
    /// pergunta ao assistente "onde acho um Corolla 2022 em Porto Alegre" - e
    /// ninguem no negocio sabe, porque o bloqueio quase sempre veio de um
    /// tutorial de "proteja seu conteudo" aplicado sem consequencia medida.
    ///
    /// Lista vazia com <see cref="HasRobotsTxt"/> verdadeiro significa "nenhum
    /// bloqueado"; nula significa "nao verificado".
    /// </summary>
    public IReadOnlyList<string>? AiCrawlersBlocked { get; init; }

    /// <summary>
    /// <c>/llms.txt</c>: convencao emergente para dizer a um modelo o que o site
    /// e e onde esta o que importa. Ausencia hoje nao e defeito - e vantagem de
    /// quem tem.
    /// </summary>
    public bool? HasLlmsTxt { get; init; }

    /// <summary>
    /// O documento se declara indexavel? Falso com <c>noindex</c> em meta robots
    /// ou no cabecalho <c>X-Robots-Tag</c>.
    ///
    /// Raro e vale medir porque o custo do falso negativo e total: um
    /// <c>noindex</c> esquecido numa migracao zera a aquisicao organica, e o
    /// sintoma que chega ao negocio e "as vendas cairam", nunca "o site saiu do
    /// indice".
    /// </summary>
    public bool? IsIndexable { get; init; }

    /// <summary>
    /// Palavras de texto visivel no HTML CRU, sem executar JavaScript.
    ///
    /// E o numero que denuncia a vitrine em SPA. O rastreador que nao executa JS
    /// - a maioria dos de IA - ve exatamente isto. Uma home de concessionaria
    /// com 40 palavras nao tem "pouco conteudo": ela tem o estoque inteiro atras
    /// de uma chamada que aquele rastreador nao faz.
    /// </summary>
    public int? RawTextWords { get; init; }

    // ---------------------------------------- legibilidade por maquina (AEO)

    /// <summary>
    /// Tipos declarados em JSON-LD (<c>@type</c>): <c>AutoDealer</c>,
    /// <c>Vehicle</c>, <c>Offer</c>, <c>FAQPage</c>, <c>LocalBusiness</c>...
    ///
    /// E o que <see cref="HasStructuredData"/> nunca conseguiu dizer. "Tem dado
    /// estruturado" nao distingue um rodape com <c>Organization</c> de uma
    /// vitrine inteira marcada com <c>Vehicle</c> e <c>Offer</c> - e e a segunda
    /// que faz o estoque ser citavel por um motor de resposta.
    /// </summary>
    public IReadOnlyList<string>? StructuredDataTypes { get; init; }

    /// <summary>
    /// O dado estruturado carrega nome, endereco e telefone juntos.
    ///
    /// E o minimo para um motor de resposta afirmar QUAL negocio e este. Sem
    /// NAP, a loja pode estar bem escrita e ainda assim ser indistinguivel de
    /// outra de nome parecido na mesma cidade.
    /// </summary>
    public bool? StructuredDataHasNap { get; init; }

    /// <summary>Quantos <c>h1</c>. Mais de um e hierarquia ambigua para quem extrai.</summary>
    public int? H1Count { get; init; }

    /// <summary>Quantos <c>h2</c>. Zero em pagina longa costuma ser texto sem estrutura.</summary>
    public int? H2Count { get; init; }

    // ----------------------------------------------- qualidade que ranqueia

    /// <summary>
    /// Comprimento do <c>title</c>. Presenca ja era medida; comprimento e o que
    /// diz se ele sobrevive ao corte do resultado de busca.
    /// </summary>
    public int? TitleLength { get; init; }

    /// <summary>Comprimento da meta description, pela mesma razao.</summary>
    public int? MetaDescriptionLength { get; init; }

    /// <summary>
    /// O canonical aponta para a propria pagina?
    ///
    /// Canonical errado e pior que canonical ausente: ausente deixa o buscador
    /// decidir; apontando para outra URL, ele obedece e tira a pagina do indice.
    /// </summary>
    public bool? CanonicalIsSelfReferencing { get; init; }

    /// <summary>Total de <c>img</c> no documento.</summary>
    public int? ImageCount { get; init; }

    /// <summary>Quantas tem <c>alt</c> nao vazio - acessibilidade e leitura por maquina.</summary>
    public int? ImagesWithAlt { get; init; }

    /// <summary>
    /// Quantas declaram largura e altura. E o proxy de CLS que da para medir sem
    /// navegador: imagem sem dimensao empurra o layout quando carrega, e num
    /// catalogo de veiculos isso acontece em cada card.
    /// </summary>
    public int? ImagesWithDimensions { get; init; }

    /// <summary>
    /// Quantas usam formato moderno (webp/avif). Numa vitrine, imagem e a maior
    /// parte do peso, e peso e tempo de carregamento - que vira custo de midia.
    /// </summary>
    public int? ImagesModernFormat { get; init; }

    /// <summary>Cabecalho <c>Strict-Transport-Security</c> presente.</summary>
    public bool? HasHsts { get; init; }

    /// <summary>Links internos - profundidade de navegacao disponivel ao rastreador.</summary>
    public int? InternalLinkCount { get; init; }

    /// <summary>Idioma declarado em <c>&lt;html lang&gt;</c>.</summary>
    public string? DeclaredLanguage { get; init; }

    // -------------------------------------------------------------- rastreio

    /// <summary>
    /// Tecnologias identificadas por assinatura no HTML. Medidas, nao inferidas:
    /// cada uma veio de um trecho literal do documento.
    /// </summary>
    public IReadOnlyList<DetectedTechnology> Technologies { get; init; } = [];

    public bool HasAnalytics => Technologies.Any(t => t.Category == TechnologyCategory.Analytics);
    public bool HasTagManager => Technologies.Any(t => t.Category == TechnologyCategory.TagManager);
    public bool HasAdsPixel => Technologies.Any(t => t.Category == TechnologyCategory.Ads);
    public bool HasChat => Technologies.Any(t => t.Category == TechnologyCategory.Chat);

    /// <summary>Quando a sonda rodou.</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    public static WebsiteProbeResult Unreachable(
        string url, string error, DateTimeOffset observedAt) => new()
    {
        RequestedUrl = url,
        Error = error,
        ObservedAt = observedAt
    };
}

/// <summary>Uma tecnologia vista no HTML, com o trecho que a denunciou.</summary>
public sealed record DetectedTechnology
{
    public required string Category { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }

    /// <summary>
    /// O trecho do documento que casou com a assinatura. Guardado para que a
    /// deteccao seja auditavel do mesmo jeito que uma evidencia do agente e -
    /// "detectamos Salesforce" sem o trecho e uma afirmacao sem lastro, ainda que
    /// a tenha feito uma regex e nao um modelo.
    /// </summary>
    public required string Match { get; init; }

    public decimal Confidence { get; init; } = 1.0m;
}

/// <summary>
/// Categorias de <c>technologies.category</c>. Constantes e nao enum: a lista
/// cresce a cada assinatura nova, e um enum exigiria migration para cada
/// descoberta - a coluna e texto pelo mesmo motivo (migration 0015).
/// </summary>
public static class TechnologyCategory
{
    public const string Analytics = "analytics";
    public const string TagManager = "tag_manager";
    public const string Ads = "ads";
    public const string Crm = "crm";
    public const string Dms = "dms";
    public const string Chat = "chat";
    public const string Cms = "cms";
    public const string InventoryPlatform = "inventory_platform";
    public const string Marketplace = "marketplace";
    public const string Ecommerce = "ecommerce";
    public const string Other = "other";
}

/// <summary>Origem de uma linha de <c>technologies</c>.</summary>
public static class TechnologySource
{
    /// <summary>Medida no HTML pela sonda. A propria medicao e a fonte.</summary>
    public const string Probe = "probe";

    /// <summary>Inferida pelo agente. Exige evidence_id - a 0015 impoe por check.</summary>
    public const string Agent = "agent";
}
