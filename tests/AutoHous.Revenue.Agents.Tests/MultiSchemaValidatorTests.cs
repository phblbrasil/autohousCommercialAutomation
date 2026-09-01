using System.Text.Json.Serialization;
using AutoHous.Revenue.Agents;

namespace AutoHous.Revenue.Agents.Tests;

/// <summary>
/// O validador escolhe o schema pelo TIPO do contrato.
///
/// Antes do Website Auditor havia um schema so, e <c>Validate&lt;T&gt;</c> o usava
/// fosse qual fosse T — correto enquanto existia um agente. Com dois, a saida do
/// auditor seria validada contra o Research Profile e reprovaria inteira, com
/// violacoes falando de campos que o auditor nunca deveria ter. O erro apareceria
/// como "o agente nao segue o contrato", que e a leitura errada.
///
/// Os contratos aqui sao LOCAIS, e nao <c>ResearchProfile</c> e
/// <c>WebsiteAuditProfile</c>. O que se testa e a SELECAO de schema por tipo; usar
/// os contratos reais obrigaria cada payload a ser um perfil completo, e a
/// primeira coisa a quebrar seria a desserializacao dos campos obrigatorios - o
/// teste falharia por um motivo que nao e o dele. Os contratos reais estao
/// cobertos pelos testes de dominio e do slice.
/// </summary>
public class MultiSchemaValidatorTests
{
    private sealed record ContratoA
    {
        [JsonPropertyName("segment")]
        public required string Segment { get; init; }
    }

    private sealed record ContratoB
    {
        [JsonPropertyName("audited_url")]
        public required string AuditedUrl { get; init; }
    }

    private const string SchemaA = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://autohous.test/contrato-a.json",
          "type": "object",
          "additionalProperties": false,
          "required": ["segment"],
          "properties": { "segment": { "type": "string" } }
        }
        """;

    private const string SchemaB = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://autohous.test/contrato-b.json",
          "type": "object",
          "additionalProperties": false,
          "required": ["audited_url"],
          "properties": { "audited_url": { "type": "string" } }
        }
        """;

    private const string PayloadB = """{"audited_url":"https://exemplo.com.br"}""";

    private static StructuredOutputValidator Build() => new(new Dictionary<Type, string>
    {
        [typeof(ContratoA)] = SchemaA,
        [typeof(ContratoB)] = SchemaB
    });

    /// <summary>
    /// O caso que motivou a mudanca: o MESMO texto e valido para um contrato e
    /// invalido para o outro, e o validador tem que separar os dois.
    /// </summary>
    [Fact]
    public void Cada_contrato_e_avaliado_contra_o_proprio_schema()
    {
        var validator = Build();

        var certo = validator.Validate<ContratoB>(PayloadB);
        Assert.True(certo.IsValid, certo.Describe());
        Assert.Equal("https://exemplo.com.br", certo.Value!.AuditedUrl);

        // Cobrado contra o schema do outro contrato, o mesmo texto falha - e
        // falha por FALTAR segment, e nao por um motivo qualquer.
        var cruzado = validator.Validate<ContratoA>(PayloadB);

        Assert.False(cruzado.IsValid);
        Assert.Contains("segment", cruzado.Describe());
    }

    /// <summary>
    /// Contrato sem schema registrado e erro NOSSO, de composicao. Ele volta como
    /// violacao e nao como excecao para que o motivo chegue ao
    /// <c>research_runs.error</c> junto das demais, em vez de virar stack trace num
    /// log. A mensagem aponta para onde se conserta.
    /// </summary>
    [Fact]
    public void Contrato_sem_schema_registrado_falha_dizendo_onde_conserta()
    {
        var incompleto = new StructuredOutputValidator(new Dictionary<Type, string>
        {
            [typeof(ContratoA)] = SchemaA
        });

        var outcome = incompleto.Validate<ContratoB>(PayloadB);

        Assert.False(outcome.IsValid);
        Assert.Contains("ContratoB", outcome.Describe());
        Assert.Contains("AddAgentValidators", outcome.Describe());
    }

    /// <summary>
    /// O construtor de schema unico continua se comportando como antes: qualquer
    /// T cai no unico schema que existe. E o que mantem verdes os testes que
    /// constroem o validador com um schema so - e o que permitiu trocar a
    /// mecanica sem reescrever a bateria inteira.
    /// </summary>
    [Fact]
    public void Construtor_de_schema_unico_segue_valendo_para_qualquer_contrato()
    {
        var validator = new StructuredOutputValidator(SchemaB);

        Assert.True(validator.Validate<ContratoB>(PayloadB).IsValid);
    }
}
