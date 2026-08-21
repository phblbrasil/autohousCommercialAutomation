using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Agents.Tests;

public class StructuredOutputValidatorTests
{
    private static StructuredOutputValidator Validator() =>
        StructuredOutputValidator.FromFile(RepoPaths.Schema("research-profile.schema.json"));

    [Fact]
    public void Valida_e_desserializa_o_fixture_de_sucesso()
    {
        var raw = File.ReadAllText(RepoPaths.Fixture("researcher", "success"));

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.True(outcome.IsValid, outcome.Describe());
        Assert.Equal("dealer_group", outcome.Value!.Segment);
        Assert.Equal(6, outcome.Value.StoreCount);
        Assert.Equal(4, outcome.Value.Evidence.Count);
        Assert.Equal(2, outcome.Value.Brands.Count);
        Assert.Single(outcome.Value.Signals);
        Assert.True(outcome.Value.DigitalPresence!.HasInventory);
    }

    [Fact]
    public void Rejeita_payload_truncado_do_modelo()
    {
        var raw = File.ReadAllText(RepoPaths.Fixture("researcher", "malformed"));

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.False(outcome.IsValid);
        Assert.NotEmpty(outcome.Violations);
    }

    [Fact]
    public void Rejeita_ausencia_de_campo_obrigatorio()
    {
        var raw = """
        {"segment": "dealership", "research_completeness": 0.8,
         "evidence": [{"claim_type":"x","claim_text":"texto suficientemente longo","confidence":0.9,
         "source":{"type":"website","url":"https://a.com","observed_at":"2026-08-19T10:00:00Z"}}]}
        """;

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Violations, v => v.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejeita_evidencia_vazia()
    {
        // Um perfil sem nenhuma evidencia nunca pode ser aceito: e a Regra 1 da
        // secao 25 no nivel do schema.
        var raw = """
        {"summary":"Um resumo com tamanho suficiente para passar no minimo exigido pelo schema.",
         "segment":"dealership","research_completeness":0.8,"evidence":[]}
        """;

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public void Rejeita_propriedade_desconhecida()
    {
        var raw = """
        {"summary":"Um resumo com tamanho suficiente para passar no minimo exigido pelo schema.",
         "segment":"dealership","research_completeness":0.8,"inventado":true,
         "evidence":[{"claim_type":"x","claim_text":"texto suficientemente longo","confidence":0.9,
         "source":{"type":"website","url":"https://a.com","observed_at":"2026-08-19T10:00:00Z"}}]}
        """;

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public void Rejeita_segmento_fora_do_enum()
    {
        var raw = """
        {"summary":"Um resumo com tamanho suficiente para passar no minimo exigido pelo schema.",
         "segment":"padaria","research_completeness":0.8,
         "evidence":[{"claim_type":"x","claim_text":"texto suficientemente longo","confidence":0.9,
         "source":{"type":"website","url":"https://a.com","observed_at":"2026-08-19T10:00:00Z"}}]}
        """;

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public void Rejeita_completude_fora_do_intervalo()
    {
        var raw = """
        {"summary":"Um resumo com tamanho suficiente para passar no minimo exigido pelo schema.",
         "segment":"dealership","research_completeness":1.7,
         "evidence":[{"claim_type":"x","claim_text":"texto suficientemente longo","confidence":0.9,
         "source":{"type":"website","url":"https://a.com","observed_at":"2026-08-19T10:00:00Z"}}]}
        """;

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public void Rejeita_fonte_sem_url_http()
    {
        var raw = """
        {"summary":"Um resumo com tamanho suficiente para passar no minimo exigido pelo schema.",
         "segment":"dealership","research_completeness":0.8,
         "evidence":[{"claim_type":"x","claim_text":"texto suficientemente longo","confidence":0.9,
         "source":{"type":"website","url":"nao-e-uma-url","observed_at":"2026-08-19T10:00:00Z"}}]}
        """;

        var outcome = Validator().Validate<ResearchProfile>(raw);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public void Descreve_violacoes_de_forma_legivel_para_o_prompt_de_reparo()
    {
        var outcome = Validator().Validate<ResearchProfile>("{}");

        Assert.False(outcome.IsValid);
        Assert.Contains("-", outcome.Describe());
    }
}
