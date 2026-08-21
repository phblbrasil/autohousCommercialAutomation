using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData("Comercial de Veículos Urca LTDA.", "COMERCIAL DE VEICULOS URCA")]
    [InlineData("COMERCIAL DE VEICULOS URCA", "COMERCIAL DE VEICULOS URCA")]
    [InlineData("Comercial de Veiculos Urca S/A", "COMERCIAL DE VEICULOS URCA")]
    [InlineData("  Comercial   de  Veículos   Urca  ME ", "COMERCIAL DE VEICULOS URCA")]
    public void Converge_variacoes_da_mesma_razao_social(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Remove_sufixos_encadeados()
    {
        Assert.Equal("AUTO PECAS SUL", NameNormalizer.Normalize("Auto Peças Sul LTDA ME"));
    }

    [Fact]
    public void Preserva_sufixo_que_faz_parte_do_nome()
    {
        // "LTDA" no inicio nao e sufixo societario: remover quebraria o nome.
        Assert.Equal("LTDA MOTORES", NameNormalizer.Normalize("Ltda Motores"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Trata_entrada_vazia(string? input)
    {
        Assert.Equal(string.Empty, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normaliza_nome_de_pessoa()
    {
        Assert.Equal("JOAO PEREIRA GONCALVES", NameNormalizer.Normalize("João Pereira Gonçalves"));
    }
}
