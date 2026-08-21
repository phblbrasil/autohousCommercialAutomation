using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

public class SearchQueryExpanderTests
{
    [Fact]
    public void Expande_substantivo_para_a_forma_verbal()
    {
        // O stemmer portugues gera stems diferentes: expansão -> 'expansa',
        // expandindo -> 'expand'. Sem expansao, quem busca "expansao" nao acha
        // "o grupo esta expandindo".
        var result = SearchQueryExpander.Expand("expansao");

        Assert.Contains("expandindo", result);
        Assert.StartsWith("expansao", result);
        Assert.Contains(" OR ", result);
    }

    [Fact]
    public void Aceita_a_forma_acentuada()
    {
        Assert.Contains("expandindo", SearchQueryExpander.Expand("expansão"));
    }

    [Fact]
    public void Termo_sem_sinonimo_passa_intacto()
    {
        Assert.Equal("hyundai bauru", SearchQueryExpander.Expand("hyundai bauru"));
    }

    [Fact]
    public void Nao_expande_busca_por_frase_exata()
    {
        // Aspas indicam intencao de frase literal; expandir mudaria o resultado.
        const string query = "\"nova unidade\"";

        Assert.Equal(query, SearchQueryExpander.Expand(query));
    }

    [Fact]
    public void Nao_puxa_sinonimo_de_termo_excluido()
    {
        var result = SearchQueryExpander.Expand("lojas -expansao");

        Assert.DoesNotContain("expandindo", result);
    }

    [Fact]
    public void Nao_duplica_sinonimo_ja_presente()
    {
        var result = SearchQueryExpander.Expand("expansao expandindo");

        Assert.Equal(1, result.Split("expandindo").Length - 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Consulta_vazia_devolve_vazio(string? query)
    {
        Assert.Equal(string.Empty, SearchQueryExpander.Expand(query));
    }

    [Fact]
    public void Expande_varios_termos_na_mesma_consulta()
    {
        var result = SearchQueryExpander.Expand("expansao contratacao");

        Assert.Contains("expandindo", result);
        Assert.Contains("contratando", result);
    }
}
