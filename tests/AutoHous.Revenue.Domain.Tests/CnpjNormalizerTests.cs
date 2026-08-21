using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

public class CnpjNormalizerTests
{
    // CNPJs sinteticos com digitos verificadores corretos.
    [Theory]
    [InlineData("11.222.333/0001-81")]
    [InlineData("11222333000181")]
    [InlineData(" 11 222 333 0001 81 ")]
    public void Aceita_cnpj_valido_com_ou_sem_mascara(string input)
    {
        Assert.True(CnpjNormalizer.IsValid(input));
        Assert.Equal("11222333000181", CnpjNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("11222333000182")]   // segundo digito verificador errado
    [InlineData("11222333000191")]   // primeiro digito verificador errado
    [InlineData("1122233300018")]    // curto demais
    [InlineData("112223330001811")]  // longo demais
    [InlineData("")]
    [InlineData(null)]
    public void Rejeita_cnpj_invalido(string? input)
    {
        Assert.False(CnpjNormalizer.IsValid(input));
    }

    [Theory]
    [InlineData("00000000000000")]
    [InlineData("11111111111111")]
    [InlineData("99999999999999")]
    public void Rejeita_digitos_repetidos_mesmo_passando_no_calculo(string input)
    {
        // 00000000000000 satisfaz a formula dos digitos verificadores, mas nao
        // e um CNPJ real. Sem esta guarda, viraria account fantasma.
        Assert.False(CnpjNormalizer.IsValid(input));
    }

    [Fact]
    public void Normalize_lanca_para_entrada_invalida()
    {
        Assert.Throws<ArgumentException>(() => CnpjNormalizer.Normalize("123"));
    }

    [Fact]
    public void TryNormalize_nao_lanca_e_sinaliza_falha()
    {
        Assert.False(CnpjNormalizer.TryNormalize("123", out var result));
        Assert.Equal(string.Empty, result);

        Assert.True(CnpjNormalizer.TryNormalize("11.222.333/0001-81", out var ok));
        Assert.Equal("11222333000181", ok);
    }

    [Fact]
    public void Format_aplica_mascara_padrao()
    {
        Assert.Equal("11.222.333/0001-81", CnpjNormalizer.Format("11222333000181"));
    }
}
