using System.Text.Json.Serialization;

namespace AutoHous.Revenue.Domain.Contracts;

/// <summary>
/// Contrato de saida do agente People Finder (A05).
///
/// Espelha <c>hermes/schemas/contact-discovery.schema.json</c>, e vale a mesma
/// regra dos outros contratos: o schema e a autoridade, e a desserializacao so
/// acontece depois do validador.
///
/// Este e o unico contrato de agente que carrega PII de pessoa fisica, e por
/// isso o unico com duas camadas de guarda em vez de uma. Alem da Regra 1 - toda
/// afirmacao com evidencia -, vale o <see cref="ContactPolicy"/>: confianca
/// minima por contato e por canal, e a distincao entre canal profissional e
/// pessoal. O ADR-0008 ja tinha decidido que <c>company_partners</c> ficaria
/// atras de opt-in por ser PII; aqui a PII e o produto do agente, e a guarda
/// precisa estar no caminho da escrita.
///
/// O que este contrato deliberadamente NAO tem: score de contactability. E
/// aritmetica do <see cref="OpportunityScoring"/> sobre o que foi gravado, e
/// pedi-la ao modelo repetiria o erro que o ADR-0005 evita.
/// </summary>
public sealed record ContactDiscoveryProfile
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("search_completeness")]
    public required decimal SearchCompleteness { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<EvidenceClaim> Evidence { get; init; } = [];

    [JsonPropertyName("contacts")]
    public IReadOnlyList<ContactClaim> Contacts { get; init; } = [];

    /// <summary>
    /// Cargos que a busca procurou e nao encontrou. Nao e enfeite: "procurei
    /// diretor de marketing nesta empresa e nao existe" e informacao comercial -
    /// significa que marketing e do socio -, e sem registro disso a proxima
    /// execucao gastaria a mesma busca de novo.
    /// </summary>
    [JsonPropertyName("searched_without_result")]
    public IReadOnlyList<string> SearchedWithoutResult { get; init; } = [];
}

public sealed record ContactClaim
{
    [JsonPropertyName("full_name")]
    public required string FullName { get; init; }

    [JsonPropertyName("job_title")]
    public string? JobTitle { get; init; }

    /// <summary>
    /// Persona sugerida pelo agente. A plataforma reclassifica com
    /// <see cref="PersonaCatalog"/> e usa a dela: o agente ve um cargo, o
    /// catalogo sabe qual produto persegue aquele cargo.
    /// </summary>
    [JsonPropertyName("persona")]
    public string? Persona { get; init; }

    [JsonPropertyName("department")]
    public string? Department { get; init; }

    [JsonPropertyName("confidence")]
    public required decimal Confidence { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }

    [JsonPropertyName("channels")]
    public IReadOnlyList<ChannelClaim> Channels { get; init; } = [];
}

public sealed record ChannelClaim
{
    /// <summary>Valor de <see cref="ContactChannel"/>. O schema restringe por enum.</summary>
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("confidence")]
    public required decimal Confidence { get; init; }

    /// <summary>
    /// Evidencia PROPRIA, e nao herdada do contato. Achar o nome de um diretor
    /// numa noticia e achar o e-mail dele sao duas descobertas, com fontes
    /// diferentes e confiabilidades diferentes - e o e-mail e o que vai ser
    /// usado para escrever para uma pessoa real.
    /// </summary>
    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}
