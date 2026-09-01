using System.Text.Json.Serialization;

namespace AutoHous.Revenue.Domain.Contracts;

/// <summary>
/// Contrato de saida do agente Website Auditor (A03).
///
/// Espelha <c>hermes/schemas/website-audit.schema.json</c>, e vale aqui a mesma
/// regra do <see cref="ResearchProfile"/>: o schema e a autoridade, e estes
/// records so podem ser desserializados DEPOIS do validador.
///
/// O que este contrato deliberadamente NAO tem: nota de desempenho, peso de
/// pagina, tempo de resposta, presenca de pixel. Sao medicoes, vem de
/// <see cref="WebsiteProbeResult"/>, e pedi-las ao modelo seria convidar uma
/// estimativa a ocupar o lugar de um fato. O agente responde o que so ele
/// consegue responder: o que a pagina significa para uma operacao automotiva.
///
/// Reaproveita <see cref="EvidenceClaim"/> do Research Profile de proposito - e a
/// mesma Regra 1, o mesmo guard e o mesmo caminho de persistencia de fonte e
/// evidencia. Dois formatos de evidencia significariam duas chances de errar.
/// </summary>
public sealed record WebsiteAuditProfile
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>URL que o agente de fato analisou.</summary>
    [JsonPropertyName("audited_url")]
    public required string AuditedUrl { get; init; }

    [JsonPropertyName("audit_completeness")]
    public required decimal AuditCompleteness { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<EvidenceClaim> Evidence { get; init; } = [];

    [JsonPropertyName("inventory")]
    public InventoryClaim? Inventory { get; init; }

    /// <summary>
    /// Onde mais o estoque desta empresa aparece. Mais de um portal e um dos
    /// cinco criterios de Technology Pain, e vira
    /// <c>website_audits.multiple_portals</c>.
    /// </summary>
    [JsonPropertyName("portals")]
    public IReadOnlyList<PortalClaim> Portals { get; init; } = [];

    /// <summary>
    /// Sistemas que a operacao aparenta usar. Aqui o agente PODE inferir - de
    /// vaga de emprego, de rodape, de subdominio - mas cada inferencia carrega
    /// evidence_index, e a 0015 recusa no banco qualquer linha de origem
    /// 'agent' sem evidencia.
    /// </summary>
    [JsonPropertyName("integrations")]
    public IReadOnlyList<IntegrationClaim> Integrations { get; init; } = [];

    [JsonPropertyName("conversion")]
    public ConversionClaim? Conversion { get; init; }

    [JsonPropertyName("issues")]
    public IReadOnlyList<AuditIssue> Issues { get; init; } = [];

    [JsonPropertyName("strengths")]
    public IReadOnlyList<AuditStrength> Strengths { get; init; } = [];
}

public sealed record InventoryClaim
{
    /// <summary>Existe vitrine de veiculos no proprio site.</summary>
    [JsonPropertyName("published_online")]
    public required bool PublishedOnline { get; init; }

    /// <summary>
    /// So quando ha contagem ou paginacao VISIVEL. Estimar pelo tamanho da
    /// empresa e exatamente o tipo de numero que nao pode existir: ele sai daqui
    /// para a mensagem comercial.
    /// </summary>
    [JsonPropertyName("approximate_count")]
    public int? ApproximateCount { get; init; }

    [JsonPropertyName("has_search_filters")]
    public bool? HasSearchFilters { get; init; }

    [JsonPropertyName("has_detail_pages")]
    public bool? HasDetailPages { get; init; }

    [JsonPropertyName("has_photos")]
    public bool? HasPhotos { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record PortalClaim
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record IntegrationClaim
{
    [JsonPropertyName("system")]
    public required string System { get; init; }

    /// <summary>Valor de <see cref="TechnologyCategory"/>.</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("confidence")]
    public required decimal Confidence { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record ConversionClaim
{
    [JsonPropertyName("has_lead_form")]
    public bool? HasLeadForm { get; init; }

    [JsonPropertyName("has_whatsapp")]
    public bool? HasWhatsApp { get; init; }

    [JsonPropertyName("has_financing_simulator")]
    public bool? HasFinancingSimulator { get; init; }

    [JsonPropertyName("has_trade_in")]
    public bool? HasTradeIn { get; init; }

    [JsonPropertyName("has_scheduling")]
    public bool? HasScheduling { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record AuditIssue
{
    /// <summary>Valor de <see cref="AuditArea"/>.</summary>
    [JsonPropertyName("area")]
    public required string Area { get; init; }

    /// <summary>"low" | "medium" | "high".</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record AuditStrength
{
    [JsonPropertyName("area")]
    public required string Area { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

/// <summary>
/// Areas de <see cref="AuditIssue.Area"/>. As tres primeiras sao as unicas que o
/// agente pode julgar sozinho; desempenho e mobile aparecem na lista porque um
/// achado qualitativo ali ainda e util ("o carrossel da home carrega 40 fotos"),
/// mas o SCORE dessas duas dimensoes vem da sonda, nunca do texto.
/// </summary>
public static class AuditArea
{
    public const string Ux = "ux";
    public const string Conversion = "conversion";
    public const string Inventory = "inventory";
    public const string Seo = "seo";
    public const string Performance = "performance";
    public const string Mobile = "mobile";
    public const string Tracking = "tracking";
}
