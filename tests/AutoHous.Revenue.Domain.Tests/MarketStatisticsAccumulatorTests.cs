namespace AutoHous.Revenue.Domain.Tests;

public class MarketStatisticsAccumulatorTests
{
    private static MarketStatisticsAccumulator Accumulate(
        params (string? Uf, string? Cnae, string? Situacao, string? MatrizFilial, string? Municipio)[] rows)
    {
        var stats = new MarketStatisticsAccumulator();

        foreach (var (uf, cnae, situacao, matriz, municipio) in rows)
        {
            stats.Observe(uf, cnae, situacao, matriz, municipio);
        }

        return stats;
    }

    [Fact]
    public void Soma_linhas_do_mesmo_recorte()
    {
        var stats = Accumulate(
            ("SP", "4511101", "02", "1", "7107"),
            ("SP", "4511101", "02", "1", "7107"),
            ("SP", "4511101", "02", "2", "7107"));

        var matriz = stats.ByCnae.Single(r => r.MatrizFilial == "1");
        var filial = stats.ByCnae.Single(r => r.MatrizFilial == "2");

        // Distinguir matriz de filial e o que separa "2 empresas" de "3 lojas".
        Assert.Equal(2, matriz.Establishments);
        Assert.Equal(1, filial.Establishments);
        Assert.Equal(3, stats.Scanned);
    }

    [Fact]
    public void Conta_tudo_inclusive_o_que_o_filtro_de_captura_descarta()
    {
        // Duas revendas baixadas e uma padaria. Nenhuma das tres entra em
        // companies_raw; todas as tres tem que aparecer aqui, porque este
        // agregado e o que impede o filtro de origem de esconder o que descartou.
        var stats = Accumulate(
            ("SP", "4511102", "08", "1", "7107"),
            ("SP", "4511102", "08", "1", "7107"),
            ("SP", "1091102", "02", "1", "7107"));

        Assert.Equal(3, stats.Scanned);
        Assert.Equal(2, stats.ByCnae.Single(r => r.Cnae == "4511102").Establishments);
        Assert.Equal(1, stats.ByCnae.Single(r => r.Cnae == "1091102").Establishments);
    }

    [Fact]
    public void Municipio_so_cruza_para_o_universo_do_catalogo()
    {
        var stats = Accumulate(
            ("SP", "4511101", "02", "1", "7107"),
            ("SP", "1091102", "02", "1", "7107"));

        // A grade municipal completa seria 5.572 municipios x ~1.350 CNAEs.
        // Ninguem consulta a concentracao municipal de padaria para vender
        // software automotivo.
        Assert.Equal("4511101", Assert.Single(stats.ByMunicipio).Cnae);
        Assert.Equal(2, stats.ByCnae.Count);
    }

    [Fact]
    public void Normaliza_o_cnae_antes_de_agrupar()
    {
        // As tres grafias do mesmo codigo tem que cair na mesma celula: agrupar
        // por string crua espalharia a mesma atividade em tres linhas conforme o
        // arquivo de origem.
        var stats = Accumulate(
            ("SP", "4511-1/01", "02", "1", "7107"),
            ("SP", "45.11-1-01", "02", "1", "7107"),
            ("SP", "4511101", "02", "1", "7107"));

        var only = Assert.Single(stats.ByCnae);

        Assert.Equal("4511101", only.Cnae);
        Assert.Equal(3, only.Establishments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Campo_ausente_vira_string_vazia_e_nao_some(string? uf)
    {
        // As colunas fazem parte da chave primaria da tabela de destino, e chave
        // nao aceita NULL. A Receita deixa UF em branco para estabelecimento no
        // exterior; descartar a linha seria perder a contagem.
        var stats = Accumulate((uf, "4511101", "02", "1", "7107"));

        var only = Assert.Single(stats.ByCnae);

        Assert.Equal(string.Empty, only.Uf);
        Assert.Equal(1, only.Establishments);
    }

    [Fact]
    public void Uf_e_normalizada_para_caixa_alta()
    {
        var stats = Accumulate(
            ("sp", "4511101", "02", "1", "7107"),
            ("SP", "4511101", "02", "1", "7107"));

        var only = Assert.Single(stats.ByCnae);

        Assert.Equal("SP", only.Uf);
        Assert.Equal(2, only.Establishments);
    }

    [Fact]
    public void Municipio_em_branco_nao_gera_celula()
    {
        var stats = Accumulate(("SP", "4511101", "02", "1", null));

        Assert.Empty(stats.ByMunicipio);
        Assert.Single(stats.ByCnae);
    }

    [Fact]
    public void Ordenacao_e_estavel_entre_execucoes()
    {
        // Dois releases da mesma base tem que produzir a mesma sequencia de
        // linhas: senao, um diff entre cargas mostra mudanca de ordem de
        // dicionario em vez de mudanca de mercado.
        var primeira = Accumulate(
            ("SP", "4511102", "02", "1", "7107"),
            ("RS", "4511101", "02", "2", "8801"),
            ("SP", "4511101", "02", "1", "7107"));

        var segunda = Accumulate(
            ("SP", "4511101", "02", "1", "7107"),
            ("SP", "4511102", "02", "1", "7107"),
            ("RS", "4511101", "02", "2", "8801"));

        Assert.Equal(primeira.ByCnae, segunda.ByCnae);
        Assert.Equal(primeira.ByMunicipio, segunda.ByMunicipio);
        Assert.Equal("RS", primeira.ByCnae[0].Uf);
    }

    [Fact]
    public void Cnae_ilegivel_e_contado_e_nao_descartado()
    {
        // Codigo que nao normaliza e provavel defeito de parsing. Somar tudo numa
        // celula visivel e o que faz o defeito aparecer; descartar em silencio e
        // o que o esconderia.
        var stats = Accumulate(("SP", "SP", "02", "1", "7107"));

        Assert.Equal("SP", Assert.Single(stats.ByCnae).Cnae);
        Assert.Empty(stats.ByMunicipio);
    }
}
