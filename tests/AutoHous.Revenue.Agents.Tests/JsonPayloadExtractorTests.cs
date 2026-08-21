using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Application;
namespace AutoHous.Revenue.Agents.Tests;

/// <summary>
/// Casos que representam o que um LLM realmente devolve. A doc oficial do Hermes
/// confirma que skills nao possuem structured output forcado, entao todos estes
/// formatos sao esperados na pratica.
/// </summary>
public class JsonPayloadExtractorTests
{
    [Fact]
    public void Aceita_json_puro()
    {
        Assert.True(JsonPayloadExtractor.TryExtract("""{"a":1}""", out var node, out _));
        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Aceita_json_cercado_em_bloco_de_codigo()
    {
        var raw = "```json\n{\"a\": 1}\n```";

        Assert.True(JsonPayloadExtractor.TryExtract(raw, out var node, out _));
        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Aceita_bloco_sem_marcador_de_linguagem()
    {
        var raw = "```\n{\"a\": 1}\n```";

        Assert.True(JsonPayloadExtractor.TryExtract(raw, out var node, out _));
        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Aceita_json_cercado_de_prosa_nos_dois_lados()
    {
        var raw = "Claro! Aqui esta o resultado:\n\n{\"a\": 1}\n\nEspero ter ajudado.";

        Assert.True(JsonPayloadExtractor.TryExtract(raw, out var node, out _));
        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Aceita_virgula_sobrando_e_comentario()
    {
        var raw = "{\n  // comentario do modelo\n  \"a\": 1,\n}";

        Assert.True(JsonPayloadExtractor.TryExtract(raw, out var node, out _));
        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Rejeita_json_truncado()
    {
        var raw = "{\"a\": 1, \"b\": ";

        Assert.False(JsonPayloadExtractor.TryExtract(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejeita_texto_sem_json()
    {
        Assert.False(JsonPayloadExtractor.TryExtract("Nao consegui pesquisar esta conta.", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejeita_resposta_vazia(string? raw)
    {
        Assert.False(JsonPayloadExtractor.TryExtract(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejeita_array_no_topo()
    {
        // O contrato do Research Profile e um objeto; um array e output errado.
        Assert.False(JsonPayloadExtractor.TryExtract("[1,2,3]", out _, out _));
    }
}
