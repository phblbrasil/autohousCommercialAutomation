using System.Text.Json.Serialization;

namespace AutoHous.Revenue.Domain.Contracts;

/// <summary>
/// Contrato de saida do agente Researcher (secao 12 do blueprint).
///
/// Espelha hermes/schemas/research-profile.schema.json. O schema e a autoridade:
/// estes records so podem ser desserializados DEPOIS que o payload passou pelo
/// validador. Nunca desserializar direto a saida crua de um LLM.
/// </summary>
public sealed record ResearchProfile
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("segment")]
    public required string Segment { get; init; }

    [JsonPropertyName("domain")]
    public string? Domain { get; init; }

    [JsonPropertyName("store_count")]
    public int? StoreCount { get; init; }

    [JsonPropertyName("inventory_estimate")]
    public int? InventoryEstimate { get; init; }

    [JsonPropertyName("research_completeness")]
    public required decimal ResearchCompleteness { get; init; }

    /// <summary>
    /// Lastro de tudo o que o agente afirma. Marcas, lojas e sinais apontam para
    /// esta lista por indice - e o que torna a Regra 1 da secao 25 verificavel.
    /// </summary>
    [JsonPropertyName("evidence")]
    public required IReadOnlyList<EvidenceClaim> Evidence { get; init; } = [];

    [JsonPropertyName("brands")]
    public IReadOnlyList<BrandClaim> Brands { get; init; } = [];

    [JsonPropertyName("locations")]
    public IReadOnlyList<LocationClaim> Locations { get; init; } = [];

    [JsonPropertyName("signals")]
    public IReadOnlyList<SignalClaim> Signals { get; init; } = [];

    [JsonPropertyName("digital_presence")]
    public DigitalPresence? DigitalPresence { get; init; }
}

public sealed record EvidenceClaim
{
    [JsonPropertyName("claim_type")]
    public required string ClaimType { get; init; }

    [JsonPropertyName("claim_text")]
    public required string ClaimText { get; init; }

    [JsonPropertyName("extracted_value")]
    public System.Text.Json.JsonElement? ExtractedValue { get; init; }

    [JsonPropertyName("confidence")]
    public required decimal Confidence { get; init; }

    [JsonPropertyName("source")]
    public required SourceRef Source { get; init; }
}

public sealed record SourceRef
{
    /// <summary>Valor do enum <c>evidence_type</c>, em snake_case.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("observed_at")]
    public required DateTimeOffset ObservedAt { get; init; }
}

public sealed record BrandClaim
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("relationship")]
    public string? Relationship { get; init; }

    [JsonPropertyName("confidence")]
    public required decimal Confidence { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record LocationClaim
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("location_type")]
    public string? LocationType { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("confidence")]
    public required decimal Confidence { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record SignalClaim
{
    [JsonPropertyName("signal_type")]
    public required string SignalType { get; init; }

    [JsonPropertyName("strength")]
    public required decimal Strength { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("observed_at")]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record DigitalPresence
{
    [JsonPropertyName("has_inventory")]
    public bool? HasInventory { get; init; }

    [JsonPropertyName("has_offers")]
    public bool? HasOffers { get; init; }

    [JsonPropertyName("has_landing_pages")]
    public bool? HasLandingPages { get; init; }
}
