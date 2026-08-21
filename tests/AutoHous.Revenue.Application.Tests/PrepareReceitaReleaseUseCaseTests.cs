using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

public class PrepareReceitaReleaseUseCaseTests
{
    private const string Release = "2026-08";

    private sealed class Harness
    {
        public FakeReceitaSourceReader Source { get; } = new();
        public FakeReceitaSpool Spool { get; } = new();
        public FakeReceitaReleaseRepository Releases { get; } = new();
        public FakeMarketStatisticsRepository Statistics { get; } = new();
        public FakeCompanyPartnerRepository Partners { get; } = new();
        public FakeUnitOfWorkFactory Uow { get; } = new();

        public PrepareReceitaReleaseUseCase Build() => new(
            Source, Spool, Releases, Statistics, Partners, Uow,
            new SequentialIdGenerator(),
            NullLogger<PrepareReceitaReleaseUseCase>.Instance);
    }

    private static ReceitaEstabelecimento Est(
        string basico,
        string ordem = "0001",
        string dv = "81",
        string cnae = "4511101",
        string situacao = "02",
        string uf = "SP",
        string matriz = "1",
        string municipio = "7107",
        string? secundarios = null) => new()
        {
            CnpjBasico = basico,
            CnpjOrdem = ordem,
            CnpjDv = dv,
            MatrizFilial = matriz,
            NomeFantasia = "Vento Sul",
            SituacaoCadastral = situacao,
            CnaePrincipal = cnae,
            CnaesSecundarios = secundarios,
            Uf = uf,
            MunicipioCodigo = municipio,
            Logradouro = "DAS PALMEIRAS",
            TipoLogradouro = "RUA",
            Numero = "120",
            Ddd1 = "14",
            Telefone1 = "32345678"
        };

    private static PrepareReceitaReleaseCommand Command(
        bool statsOnly = false,
        bool dryRun = false,
        bool activeOnly = true,
        bool socios = false,
        bool secundario = false,
        IReadOnlySet<string>? ufs = null,
        long? limit = null) => new()
        {
            Release = Release,
            StatsOnly = statsOnly,
            DryRun = dryRun,
            ActiveOnly = activeOnly,
            IncludeSocios = socios,
            IncludeSecondaryCnae = secundario,
            Ufs = ufs,
            MaxEstablishments = limit
        };

    private static async Task<List<RawCompanyRow>> RowsAsync(PrepareReceitaReleaseResult result)
    {
        var rows = new List<RawCompanyRow>();

        await foreach (var row in result.Rows) rows.Add(row);

        return rows;
    }

    // ------------------------------------------------------------- filtro

    [Fact]
    public async Task Seleciona_apenas_o_universo_do_catalogo()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.AddRange([
            Est("11222333"),                    // concessionaria
            Est("11444777", cnae: "1091102"),   // padaria
            Est("22333444", cnae: "4520001")    // oficina: universo, fora do ICP central
        ]);

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.EstablishmentsScanned);
        Assert.Equal(2, result.EstablishmentsSelected);
    }

    [Fact]
    public async Task Situacao_inativa_e_descartada_por_padrao()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.AddRange([Est("11222333"), Est("11444777", situacao: "08")]);

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.EstablishmentsSelected);
    }

    [Fact]
    public async Task Incluir_inativos_dobra_o_que_entra_e_nao_muda_o_agregado()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.AddRange([Est("11222333"), Est("11444777", situacao: "08")]);

        var result = await harness.Build().ExecuteAsync(
            Command(activeOnly: false), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.EstablishmentsSelected);

        // O agregado ve as duas nos dois modos: e ele que impede o filtro de
        // esconder o que descartou.
        Assert.Equal(2, harness.Statistics.ByCnae.Sum(r => r.Establishments));
    }

    [Fact]
    public async Task Recorte_por_uf_e_aplicado_na_origem()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.AddRange([Est("11222333"), Est("11444777", uf: "RS")]);

        var result = await harness.Build().ExecuteAsync(
            Command(ufs: new HashSet<string> { "SP" }), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.EstablishmentsSelected);
        // A do RS continua contada.
        Assert.Equal(2, harness.Statistics.ByCnae.Sum(r => r.Establishments));
    }

    [Fact]
    public async Task Cnae_secundario_so_entra_com_a_flag()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(
            Est("11222333", cnae: "1091102", secundarios: "4520001,4511102"));

        var semFlag = await new Harness().Build()
            .ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        var comFlag = await harness.Build()
            .ExecuteAsync(Command(secundario: true), TestContext.Current.CancellationToken);

        Assert.Equal(0, semFlag.EstablishmentsSelected);
        Assert.Equal(1, comFlag.EstablishmentsSelected);
    }

    // -------------------------------------------------------------- juncao

    [Fact]
    public async Task Junta_razao_social_e_porte_do_arquivo_empresas()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));
        harness.Source.Empresas.Add(new ReceitaEmpresa
        {
            CnpjBasico = "11222333",
            RazaoSocial = "GRUPO VENTO SUL VEICULOS LTDA",
            Porte = "05",
            CapitalSocial = "1250000,50",
            NaturezaJuridica = "2062"
        });
        harness.Source.Simples.Add(new ReceitaSimples
        {
            CnpjBasico = "11222333",
            OpcaoSimples = "N",
            OpcaoMei = "N"
        });
        harness.Source.Tables = new ReceitaDomainTables
        {
            Naturezas = new Dictionary<string, string> { ["2062"] = "Sociedade Empresaria Limitada" },
            Municipios = new Dictionary<string, string> { ["7107"] = "BAURU" }
        };

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);
        var row = Assert.Single(await RowsAsync(result));

        Assert.Equal("GRUPO VENTO SUL VEICULOS LTDA", row.RazaoSocial);
        Assert.Equal("05", row.Porte);
        Assert.Equal("1250000,50", row.CapitalSocial);
        Assert.Equal("Sociedade Empresaria Limitada", row.NaturezaJuridica);
        Assert.Equal("N", row.OpcaoSimples);
        Assert.Equal(1, result.CompaniesJoined);
    }

    [Fact]
    public async Task Municipio_vira_nome_e_nao_codigo()
    {
        // Sem este join, companies_cnpj.municipio receberia "7107", e tanto a
        // busca por cidade quanto a regra de mesma UF do account graph parariam.
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));
        harness.Source.Tables = new ReceitaDomainTables
        {
            Municipios = new Dictionary<string, string> { ["7107"] = "BAURU" }
        };

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);
        var row = Assert.Single(await RowsAsync(result));

        Assert.Equal("BAURU", row.Municipio);
        Assert.Equal("7107", row.MunicipioCodigo);
    }

    [Fact]
    public async Task Codigo_sem_par_na_tabela_de_dominio_volta_cru()
    {
        // A Receita publica codigo novo antes de atualizar a tabela de dominio.
        // Perder o dado seria pior que exibi-lo cru.
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333", municipio: "9999"));

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal("9999", Assert.Single(await RowsAsync(result)).Municipio);
    }

    [Fact]
    public async Task Empresa_sem_par_nao_perde_a_linha()
    {
        // Sobra o nome fantasia. A linha sem nenhum dos dois sera rejeitada com
        // "missing_name" pelo normalizador, com o motivo gravado.
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);
        var row = Assert.Single(await RowsAsync(result));

        Assert.Null(row.RazaoSocial);
        Assert.Equal("Vento Sul", row.NomeFantasia);
        Assert.Equal(0, result.CompaniesJoined);
    }

    [Fact]
    public async Task Ddd_e_telefone_viram_um_numero_so()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal("14 32345678", Assert.Single(await RowsAsync(result)).Telefone1);
    }

    [Fact]
    public async Task Matrizes_saem_antes_das_filiais()
    {
        // A regra 1 do AccountGroupResolver e raiz de CNPJ: com a matriz ja na
        // base, a filial anexa por identidade em vez de disputar trigrama contra
        // o nome dela mesma.
        var harness = new Harness();
        harness.Source.Estabelecimentos.AddRange([
            Est("11222333", ordem: "0002", dv: "62", matriz: "2"),
            Est("11444777", ordem: "0003", dv: "43", matriz: "2"),
            Est("11222333", matriz: "1")
        ]);

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);
        var rows = await RowsAsync(result);

        Assert.Equal("1", rows[0].MatrizFilial);
        Assert.Equal(["2", "2"], rows[1..].Select(r => r.MatrizFilial));
    }

    // ---------------------------------------------------------- agregado

    [Fact]
    public async Task Agregado_e_gravado_uma_vez_por_carga()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.AddRange([Est("11222333"), Est("11444777", cnae: "1091102")]);
        harness.Source.Tables = new ReceitaDomainTables
        {
            Municipios = new Dictionary<string, string> { ["7107"] = "BAURU" }
        };

        await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Statistics.Writes);
        Assert.Equal(2, harness.Statistics.ByCnae.Count);
        // Municipio so cruza para o universo do catalogo.
        Assert.Equal("4511101", Assert.Single(harness.Statistics.ByMunicipio).Cnae);
        Assert.Equal("BAURU", harness.Statistics.MunicipioNames["7107"]);
    }

    [Fact]
    public async Task Stats_only_nao_captura_empresa_nenhuma()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var result = await harness.Build().ExecuteAsync(
            Command(statsOnly: true), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.EstablishmentsScanned);
        Assert.Equal(1, harness.Statistics.Writes);
        Assert.Empty(await RowsAsync(result));

        // Sem passada sobre Empresas: ela existe para juntar o que nao vamos usar.
        Assert.False(harness.Source.Scans.ContainsKey("empresas"));
    }

    [Fact]
    public async Task Stats_only_devolve_o_agregado_no_resultado()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var result = await harness.Build().ExecuteAsync(
            Command(statsOnly: true), TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Statistics);
    }

    [Fact]
    public async Task Carga_normal_nao_carrega_o_agregado_no_resultado()
    {
        // Ele ja esta no banco; devolve-lo manteria centenas de milhares de
        // registros vivos durante toda a ingestao, sem ninguem para le-los.
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var result = await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Statistics);
    }

    // ------------------------------------------------------------ ensaio

    [Fact]
    public async Task Dry_run_nao_grava_nada()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var result = await harness.Build().ExecuteAsync(
            Command(dryRun: true), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.EstablishmentsScanned);
        Assert.Equal(0, harness.Statistics.Writes);
        Assert.Empty(harness.Releases.Started);
        Assert.Empty(harness.Spool.Files);
        Assert.Equal(0, harness.Uow.CommitCount);
    }

    [Fact]
    public async Task Limite_para_a_leitura_no_numero_pedido()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.AddRange(
            Enumerable.Range(1, 10).Select(i => Est($"1122233{i}")));

        var result = await harness.Build().ExecuteAsync(
            Command(dryRun: true, limit: 4), TestContext.Current.CancellationToken);

        Assert.Equal(4, result.EstablishmentsScanned);
    }

    [Fact]
    public async Task Limite_fora_do_ensaio_e_recusado()
    {
        // Leitura parcial produz agregado parcial. Grava-lo como se fosse o
        // mercado seria pior que nao ter numero nenhum.
        var harness = new Harness();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => harness.Build().ExecuteAsync(
            Command(limit: 100), TestContext.Current.CancellationToken));

        Assert.Contains("DryRun", error.Message);
    }

    // ------------------------------------------------------------ socios

    [Fact]
    public async Task Socios_nao_sao_lidos_sem_a_flag()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));
        harness.Source.Socios.Add(new ReceitaSocio { CnpjBasico = "11222333", Nome = "MARIA" });

        await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Empty(harness.Partners.Partners);
        Assert.False(harness.Source.Scans.ContainsKey("socios"));
        Assert.DoesNotContain(harness.Source.Requested, f => f.HasFlag(ReceitaFileSet.Socios));
    }

    [Fact]
    public async Task Socios_carregam_so_das_empresas_selecionadas()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));
        harness.Source.Socios.AddRange([
            new ReceitaSocio { CnpjBasico = "11222333", Nome = "MARIA", CpfCnpj = "***456789**", DataEntrada = "20150312" },
            // Socio de uma padaria: nao esta no recorte e nao pode entrar na base.
            new ReceitaSocio { CnpjBasico = "99999999", Nome = "JOAO" }
        ]);

        var result = await harness.Build().ExecuteAsync(
            Command(socios: true), TestContext.Current.CancellationToken);

        var partner = Assert.Single(harness.Partners.Partners);

        Assert.Equal("MARIA", partner.Nome);
        Assert.Equal("***456789**", partner.CpfCnpjMascarado);
        Assert.Equal(new DateOnly(2015, 3, 12), partner.DataEntrada);
        Assert.Equal(1, result.PartnersLoaded);
    }

    // ----------------------------------------------------------- lineage

    [Fact]
    public async Task Release_e_registrado_com_os_arquivos_e_o_progresso()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        await harness.Build().ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(Release, Assert.Single(harness.Releases.Started));
        Assert.Equal("ABC", Assert.Single(harness.Releases.Files).Sha256);
        Assert.Contains(harness.Releases.Progress, p => p.Status == ReceitaReleaseStatus.Streamed);
    }

    [Fact]
    public async Task Complete_fecha_o_release_com_o_lote()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var useCase = harness.Build();
        await useCase.ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        var batchId = Guid.CreateVersion7();
        await useCase.CompleteAsync(
            Release, batchId, ReceitaReleaseStatus.Loaded, null, TestContext.Current.CancellationToken);

        Assert.Equal(batchId, harness.Releases.Summaries[Release].BatchId);
        Assert.Equal(ReceitaReleaseStatus.Loaded, Assert.Single(harness.Releases.Finished).Status);
    }

    [Fact]
    public async Task Recarga_recomeca_o_spool_em_vez_de_acumular()
    {
        var harness = new Harness();
        harness.Source.Estabelecimentos.Add(Est("11222333"));

        var useCase = harness.Build();

        await useCase.ExecuteAsync(Command(), TestContext.Current.CancellationToken);
        var segunda = await useCase.ExecuteAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Single(await RowsAsync(segunda));
    }
}
