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
