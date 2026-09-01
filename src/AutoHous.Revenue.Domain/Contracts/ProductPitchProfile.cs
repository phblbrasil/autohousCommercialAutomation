using System.Text.Json.Serialization;

namespace AutoHous.Revenue.Domain.Contracts;

/// <summary>
/// Contrato de saida do agente Product Matcher (A04).
///
/// Espelha <c>hermes/schemas/product-pitch.schema.json</c>, e vale a mesma regra
/// dos outros dois contratos: o schema e a autoridade, e estes records so podem
/// ser desserializados DEPOIS do validador.
///
/// O que este contrato deliberadamente NAO tem: nota de fit, ranking, escolha de
/// porta de entrada. Isso e <see cref="ProductFitScoring"/>, e o ADR-0005
/// explica por que: "por que o MotorHub caiu de 78 para 51?" precisa de resposta
/// auditavel, e um numero gerado por modelo nao tem.
///
/// O agente entra onde a aritmetica nao alcanca: transformar
/// "canais_externos: 25/25 - estoque em 3 canais externos" em uma frase que um
/// diretor de operacoes reconhece como o problema dele, e antecipar o que ele
/// vai responder.
/// </summary>
public sealed record ProductPitchProfile
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>
    /// Lastro. Mesmo formato do Research Profile e da auditoria de proposito: a
    /// tese comercial cita as MESMAS paginas que a pesquisa citou, e um segundo
    /// formato de evidencia seria uma segunda chance de errar.
    /// </summary>
    [JsonPropertyName("evidence")]
    public required IReadOnlyList<EvidenceClaim> Evidence { get; init; } = [];

    [JsonPropertyName("pitches")]
    public required IReadOnlyList<ProductPitch> Pitches { get; init; } = [];

    /// <summary>
    /// Motivos para NAO abordar esta conta agora: recuperacao judicial, encerrou
    /// atividade, ja e cliente de quem nos revende, mudou de ramo.
    ///
    /// Existe porque o caminho barato de descobrir isso e aqui, antes do custo
    /// do People Finder e do constrangimento de uma abordagem errada. Um achado
    /// destes vira sinal negativo e tira a conta da fila - nunca automaticamente
    /// da suppression list, que e decisao humana (Regra 2).
    /// </summary>
    [JsonPropertyName("disqualifiers")]
    public IReadOnlyList<Disqualifier> Disqualifiers { get; init; } = [];
}

public sealed record ProductPitch
{
    /// <summary>Nome exato do catalogo. O schema restringe por enum.</summary>
    [JsonPropertyName("product")]
    public required string Product { get; init; }

    /// <summary>
    /// A frase de entrada, ancorada em um fato observado. E o unico campo que
    /// chega quase inalterado ao SDR, e por isso o que mais precisa de lastro.
    /// </summary>
    [JsonPropertyName("angle")]
    public required string Angle { get; init; }

    [JsonPropertyName("reasons")]
    public required IReadOnlyList<PitchClaim> Reasons { get; init; } = [];

    [JsonPropertyName("objections")]
    public IReadOnlyList<Objection> Objections { get; init; } = [];

    /// <summary>
    /// Subconjunto das personas do catalogo para ESTE produto. O agente pode
    /// restringir - "aqui quem decide e o socio, nao existe diretor de
    /// marketing" - e nao pode inventar cargo fora da lista.
    /// </summary>
    [JsonPropertyName("recommended_personas")]
    public IReadOnlyList<string> RecommendedPersonas { get; init; } = [];

    [JsonPropertyName("confidence")]
    public required decimal Confidence { get; init; }
}

public sealed record PitchClaim
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record Objection
{
    /// <summary>O que o interlocutor provavelmente responde.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>Como responder sem prometer o que nao foi verificado.</summary>
    [JsonPropertyName("response")]
    public required string Response { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}

public sealed record Disqualifier
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("evidence_index")]
    public required int EvidenceIndex { get; init; }
}
