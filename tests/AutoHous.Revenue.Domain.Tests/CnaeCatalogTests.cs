namespace AutoHous.Revenue.Domain.Tests;

/// <summary>
/// O catalogo decide duas coisas diferentes, e confundi-las sai caro:
/// **se** a empresa entra na base (pertencer ao universo) e **em que fila** ela
/// entra (a camada de ICP).
///
/// Os codigos abaixo estao escritos um a um de proposito. Uma camada errada nao
/// derruba build nem teste de fluxo: ela desloca dezenas de milhares de contas
/// de fila em silencio - na competencia 2026-08, mover so o CNAE de oficina
/// mecanica mudaria 279.621 estabelecimentos de lugar.
/// </summary>
public class CnaeCatalogTests
{
    [Theory]
    [InlineData("4511-1/01")]
    [InlineData("45.11-1-01")]
    [InlineData("4511101")]
    [InlineData(" 4511 1 01 ")]
    public void Reconhece_o_mesmo_codigo_em_formatos_diferentes(string raw)
    {
        // As bases publicas circulam o mesmo CNAE em pelo menos tres grafias.
        // Comparar string crua faria a mesma empresa cair em ramos diferentes
        // conforme o arquivo de origem.
        Assert.Equal("4511101", CnaeCatalog.NormalizeCode(raw));
        Assert.Equal(AutomotiveOperation.Concessionaria, CnaeCatalog.Classify(raw)!.Operation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SP")]
    [InlineData("45111")]
    [InlineData("451110123")]
    public void Codigo_ilegivel_nao_normaliza(string? raw)
    {
        Assert.Null(CnaeCatalog.NormalizeCode(raw));
        Assert.Null(CnaeCatalog.Classify(raw));
    }

    [Fact]
    public void Codigo_valido_fora_do_universo_nao_classifica()
    {
        // 1091-1/02 e padaria: codigo perfeitamente valido, so nao e nosso.
        Assert.NotNull(CnaeCatalog.NormalizeCode("1091-1/02"));
        Assert.Null(CnaeCatalog.Classify("1091-1/02"));
    }

    [Fact]
    public void Concessionaria_e_revenda_estao_no_icp_central()
    {
        Assert.True(CnaeCatalog.Classify("4511101")!.InCoreIcp);
        Assert.True(CnaeCatalog.Classify("4511102")!.InCoreIcp);
    }

    [Fact]
    public void Oficina_e_autopecas_ficam_fora_do_icp_central_mas_nao_fora_do_mapa()
    {
        // Antes eram so "false": o universo inteiro que nao vendia veiculo cabia
        // num unico booleano. Sao 593 mil estabelecimentos ativos - a camada
        // propria existe para que eles tenham fila, e nao ausencia de fila.
        Assert.False(CnaeCatalog.Classify("4520001")!.InCoreIcp);
        Assert.False(CnaeCatalog.Classify("4530703")!.InCoreIcp);

        Assert.Equal(IcpTier.Aftermarket, CnaeCatalog.TierOf("4520001"));
        Assert.Equal(IcpTier.Aftermarket, CnaeCatalog.TierOf("4530703"));
    }

    [Fact]
    public void ICP_central_e_exatamente_quem_vende_veiculo()
    {
        string[] esperado =
        [
            "4511101", // concessionaria de novos
            "4511102", // revenda de usados
            "4511103", // atacado de automoveis
            "4511104", // atacado de caminhoes
            "4512901", // representante comercial
            "4512902", // consignacao
            "4541203", // motos novas
            "4541204"  // motos usadas
        ];

        Assert.Equal(
            esperado.Order(),
            CnaeCatalog.CodesInTier(IcpTier.Core).Order());
    }

    [Fact]
    public void Aftermarket_e_oficina_e_autopecas()
    {
        string[] esperado =
        [
            "4520001", // manutencao e reparacao mecanica
            "4520002", // lanternagem, funilaria e pintura
            "4530701", // autopecas atacado
            "4530703"  // autopecas varejo
        ];

        Assert.Equal(
            esperado.Order(),
            CnaeCatalog.CodesInTier(IcpTier.Aftermarket).Order());
    }

    [Fact]
    public void Lavagem_e_locacao_ficam_no_adjacente()
    {
        // Lavagem e polimento e servico automotivo, mas de ticket e motion que
        // nenhum produto AutoHous atende hoje. Promover e uma linha no catalogo.
        Assert.Equal(IcpTier.Adjacent, CnaeCatalog.TierOf("4520005"));
        Assert.Equal(IcpTier.Adjacent, CnaeCatalog.TierOf("7711000"));
    }

    [Fact]
    public void InCoreIcp_continua_respondendo_pela_camada_central()
    {
        // A propriedade derivada existe para nao espalhar comparacao de enum;
        // se ela divergir da camada, o filtro de prospeccao mente.
        foreach (var codigo in CnaeCatalog.Codes)
        {
            var classificacao = CnaeCatalog.Classify(codigo)!;

            Assert.Equal(classificacao.Tier == IcpTier.Core, classificacao.InCoreIcp);
        }
    }

    [Fact]
    public void Toda_camada_tem_ao_menos_um_codigo()
    {
        // Camada vazia e sinal de catalogo editado pela metade.
        foreach (var camada in Enum.GetValues<IcpTier>())
        {
            Assert.NotEmpty(CnaeCatalog.CodesInTier(camada));
        }
    }
}
