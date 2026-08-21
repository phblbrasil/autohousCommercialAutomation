using AutoHous.Revenue.Application;
using AutoHous.Revenue.Infrastructure;
using AutoHous.Revenue.ReceitaFederal;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// Camada 01 de ponta a ponta contra Postgres real: zip da Receita ->
/// companies_raw -> account graph -> agregado de mercado.
///
/// O release e sintetico mas o formato e o oficial - zip com entrada de nome
/// opaco, CSV posicional sem cabecalho, ISO-8859-1. O que so aparece aqui:
/// a decodificacao latin1 chegando intacta ao banco, o join de municipio por
/// codigo, o upsert de <c>account_locations</c> e a matriz entrando antes da
/// filial no account graph.
/// </summary>
public class ReceitaLoadPipelineTests : IAsyncLifetime
{
    private const string Release = "2026-08";

    private readonly PostgresFixture _postgres = new();
    private string _work = null!;
    private ServiceProvider _services = null!;
    private ReceitaReleaseBuilder _release = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();

        _work = Directory.CreateTempSubdirectory("receita-load").FullName;
        _release = new ReceitaReleaseBuilder(Path.Combine(_work, "cache"), Release);
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        await _postgres.DisposeAsync();

        Directory.Delete(_work, recursive: true);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>
    /// Compoe o host real da carga: infraestrutura, casos de uso e a fonte da
    /// Receita, com a origem apontando para os zips ja gravados no cache.
    /// </summary>
    private void Compose()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRevenueInfrastructure(_postgres.ConnectionString);
        services.AddRevenueUseCases();

        services.AddReceitaFederalSource(o =>
        {
            o.CacheDirectory = Path.Combine(_work, "cache");
            o.WorkDirectory = Path.Combine(_work, "spool");
        });

        // Unica peca trocada: a origem. Zip, layout, cache, spool, casos de uso e
        // repositorios sao os de producao.
        services.AddSingleton(_release.AsArchive());

        _services = services.BuildServiceProvider();
    }

    private async Task<(PrepareReceitaReleaseResult Prepared, IngestCompanyBatchResult Captured, ResolveAccountGraphResult Graph)>
        LoadAsync(PrepareReceitaReleaseCommand? command = null)
    {
        Compose();

        var prepared = await Get<PrepareReceitaReleaseUseCase>().ExecuteAsync(
            command ?? new PrepareReceitaReleaseCommand { Release = Release }, Ct);

        var captured = await Get<IngestCompanyStreamUseCase>().ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = $"receita-{Release}",
            Rows = prepared.Rows
        }, Ct);

        var graph = await Get<ResolveAccountGraphUseCase>().ExecuteAsync(captured.BatchId, Ct);

        return (prepared, captured, graph);
    }

    private Task<T> ScalarAsync<T>(string sql, object? parameters = null) =>
        TestData.ScalarAsync<T>(_postgres.ConnectionString, sql, parameters);

    // ------------------------------------------------------------------ carga

    [Fact]
    public async Task Release_vira_conta_com_cadastro_completo()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(ReceitaReleaseBuilder.Estabelecimento("11222333"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"))
            .WithSimples(("11222333", "N", "N"));

        var (prepared, captured, graph) = await LoadAsync();

        Assert.Equal(1, prepared.EstablishmentsSelected);
        Assert.Equal(1, captured.AcceptedRows);
        Assert.Equal(1, graph.CreatedAccounts);

        var company = await ScalarAsync<string>(
            "select razao_social from companies_cnpj where cnpj = '11222333000181'");

        Assert.Equal("GRUPO VENTO SUL VEICULOS LTDA", company);

        // O que so a fonte oficial traz, e que nascia vazio no schema.
        Assert.Equal("05", await ScalarAsync<string>(
            "select porte from companies_cnpj where cnpj = '11222333000181'"));

        Assert.Equal(1_250_000.50m, await ScalarAsync<decimal>(
            "select capital_social from companies_cnpj where cnpj = '11222333000181'"));

        Assert.Equal("Sociedade Empresária Limitada", await ScalarAsync<string>(
            "select natureza_juridica from companies_cnpj where cnpj = '11222333000181'"));

        Assert.Equal(new DateOnly(2010, 3, 12), await ScalarAsync<DateOnly>(
            "select data_abertura from companies_cnpj where cnpj = '11222333000181'"));
    }

    [Fact]
    public async Task Municipio_chega_ao_banco_como_nome_e_nao_como_codigo()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(ReceitaReleaseBuilder.Estabelecimento("11222333"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        await LoadAsync();

        // Sem o join com Municipios.zip isto seria "7107".
        Assert.Equal("Bauru", await ScalarAsync<string>(
            "select municipio from companies_cnpj where cnpj = '11222333000181'"));

        Assert.Equal("7107", await ScalarAsync<string>(
            "select municipio_codigo from companies_cnpj where cnpj = '11222333000181'"));
    }

    [Fact]
    public async Task Acentuacao_latin1_sobrevive_ate_o_banco()
    {
        // Ler o zip como UTF-8 nao falha: corrompe a razao social em silencio, e
        // o defeito so aparece num e-mail para o cliente.
        _release
            .WithDomainTables()
            .WithEstabelecimentos(ReceitaReleaseBuilder.Estabelecimento("11222333"))
            .WithEmpresas(("11222333", "COMÉRCIO DE VEÍCULOS SÃO JOÃO LTDA", "05"));

        await LoadAsync();

        Assert.Equal("COMÉRCIO DE VEÍCULOS SÃO JOÃO LTDA", await ScalarAsync<string>(
            "select razao_social from companies_cnpj where cnpj = '11222333000181'"));
    }

    [Fact]
    public async Task Matriz_e_filial_da_mesma_raiz_caem_na_mesma_conta()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(
                // A filial vem ANTES no arquivo. A ordenacao do spool e o que
                // garante que a matriz entre primeiro e a filial anexe por raiz.
                ReceitaReleaseBuilder.Estabelecimento("11222333", ordem: "0002", dv: "62", matrizFilial: "2",
                    fantasia: "VENTO SUL MARILIA", logradouro: "DAS ACACIAS", numero: "45"),
                ReceitaReleaseBuilder.Estabelecimento("11222333", matrizFilial: "1"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        var (_, _, graph) = await LoadAsync();

        Assert.Equal(1, graph.CreatedAccounts);
        Assert.Equal(1, graph.AttachedCnpjs);
        Assert.Equal(0, graph.ReviewCandidates);

        Assert.Equal(1, await ScalarAsync<long>("select count(*) from accounts"));
        Assert.Equal(2, await ScalarAsync<long>("select count(*) from companies_cnpj"));
    }

    [Fact]
    public async Task Cada_estabelecimento_vira_uma_loja_da_conta()
    {
        // E o que faz accounts.store_count deixar de ser um campo vazio: um grupo
        // com dois CNPJs ativos e um grupo de duas lojas.
        _release
            .WithDomainTables()
            .WithEstabelecimentos(
                ReceitaReleaseBuilder.Estabelecimento("11222333", matrizFilial: "1"),
                ReceitaReleaseBuilder.Estabelecimento("11222333", ordem: "0002", dv: "62", matrizFilial: "2",
                    logradouro: "DAS ACACIAS", numero: "45"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        await LoadAsync();

        Assert.Equal(2, await ScalarAsync<long>("select count(*) from account_locations"));

        Assert.Equal(1, await ScalarAsync<long>(
            "select count(*) from account_locations where location_type = 'matriz'"));

        // O logradouro entra no nome porque a identidade da tabela e
        // (conta, nome, cidade): duas lojas do mesmo grupo na mesma cidade
        // colapsariam em uma se o nome fosse so o da bandeira.
        Assert.Equal(2, await ScalarAsync<long>(
            "select count(distinct name) from account_locations"));
    }

    [Fact]
    public async Task Recarga_do_mesmo_release_atualiza_sem_duplicar()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(ReceitaReleaseBuilder.Estabelecimento("11222333"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        await LoadAsync();
        await _services.DisposeAsync();
        _services = null!;

        await LoadAsync();

        Assert.Equal(1, await ScalarAsync<long>("select count(*) from accounts"));
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from companies_cnpj"));
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from account_locations"));

        // Um registro de release por competencia: duas linhas com contagens
        // diferentes para "2026-08" nao teriam como ser desempatadas depois.
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from receita_releases"));
    }

    // -------------------------------------------------------------- agregado

    [Fact]
    public async Task Agregado_conta_o_que_o_filtro_descartou()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(
                ReceitaReleaseBuilder.Estabelecimento("11222333"),
                // Padaria: fora do universo, nao entra em companies_raw.
                ReceitaReleaseBuilder.Estabelecimento("11444777", cnae: "1091102"),
                // Revenda baixada: dentro do universo, fora do recorte ativo.
                ReceitaReleaseBuilder.Estabelecimento("22333444", cnae: "4511102", situacao: "08"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        var (prepared, _, _) = await LoadAsync();

        Assert.Equal(3, prepared.EstablishmentsScanned);
        Assert.Equal(1, prepared.EstablishmentsSelected);

        // As tres continuam contadas: e isso que impede o filtro de origem de
        // esconder o que descartou.
        Assert.Equal(3, await ScalarAsync<long>(
            "select sum(establishments) from rf_cnae_stats where release = @Release",
            new { Release }));

        Assert.Equal(1, await ScalarAsync<long>(
            "select establishments from rf_cnae_stats where release = @Release and cnae = '1091102'",
            new { Release }));

        // Municipio so cruza para o universo do catalogo — padaria fica de fora.
        Assert.Equal(0, await ScalarAsync<long>(
            "select count(*) from rf_municipio_stats where cnae = '1091102'"));

        Assert.Equal("BAURU", await ScalarAsync<string>(
            "select distinct municipio_nome from rf_municipio_stats where municipio_codigo = '7107'"));
    }

    [Fact]
    public async Task Recarga_substitui_o_agregado_em_vez_de_somar()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(ReceitaReleaseBuilder.Estabelecimento("11222333"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        await LoadAsync();
        await _services.DisposeAsync();
        _services = null!;

        await LoadAsync();

        Assert.Equal(1, await ScalarAsync<long>(
            "select sum(establishments) from rf_cnae_stats where release = @Release", new { Release }));
    }

    // ---------------------------------------------------------------- socios

    [Fact]
    public async Task Socios_so_entram_com_a_flag_e_so_das_empresas_selecionadas()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(
                ReceitaReleaseBuilder.Estabelecimento("11222333"),
                ReceitaReleaseBuilder.Estabelecimento("11444777", cnae: "1091102"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"))
            .WithSocios(
                ("11222333", "MARIA DA SILVA", "***456789**"),
                // Socio da padaria: fora do recorte, nao pode entrar na base.
                ("11444777", "JOAO DOS SANTOS", "***123456**"));

        await LoadAsync(new PrepareReceitaReleaseCommand { Release = Release, IncludeSocios = true });

        Assert.Equal(1, await ScalarAsync<long>("select count(*) from company_partners"));

        Assert.Equal("MARIA DA SILVA", await ScalarAsync<string>(
            "select nome from company_partners where cnpj_basico = '11222333'"));

        // O CPF ja chega mascarado da origem e e guardado exatamente assim.
        Assert.Equal("***456789**", await ScalarAsync<string>(
            "select cpf_cnpj_mascarado from company_partners where cnpj_basico = '11222333'"));
    }

    [Fact]
    public async Task Sem_a_flag_nenhum_socio_e_gravado()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(ReceitaReleaseBuilder.Estabelecimento("11222333"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"))
            .WithSocios(("11222333", "MARIA DA SILVA", "***456789**"));

        await LoadAsync();

        Assert.Equal(0, await ScalarAsync<long>("select count(*) from company_partners"));
    }

    // -------------------------------------------------------------- lineage

    [Fact]
    public async Task Release_registra_arquivos_com_sha256_e_o_lote()
    {
        _release
            .WithDomainTables()
            .WithEstabelecimentos(ReceitaReleaseBuilder.Estabelecimento("11222333"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        var (_, captured, _) = await LoadAsync();

        await Get<PrepareReceitaReleaseUseCase>().CompleteAsync(
            Release, captured.BatchId, ReceitaReleaseStatus.Loaded, null, Ct);

        var summary = await Get<IReceitaReleaseRepository>().GetAsync(Release, Ct);

        Assert.NotNull(summary);
        Assert.Equal(ReceitaReleaseStatus.Loaded, summary.Status);
        Assert.Equal(captured.BatchId, summary.BatchId);
        Assert.NotNull(summary.FinishedAt);

        // O SHA-256 de cada zip e o que sustenta a promessa de que reimportar o
        // mesmo release da o mesmo resultado.
        var files = await ScalarAsync<long>(
            "select jsonb_array_length(files) from receita_releases where release = @Release",
            new { Release });

        Assert.True(files >= 7, $"esperava ao menos 7 arquivos registrados, veio {files}");

        Assert.Equal(64, await ScalarAsync<int>(
            "select length(files -> 0 ->> 'Sha256') from receita_releases where release = @Release",
            new { Release }));
    }

    [Fact]
    public async Task Rejeicao_continua_auditavel_em_companies_raw()
    {
        // O filtro de origem so testa CNAE, situacao e UF. Tudo o que exige
        // julgamento - digito verificador, nome ausente - continua sendo decidido
        // pelo normalizador, DEPOIS que a linha ja esta gravada. Ver ADR-0007.
        _release
            .WithDomainTables()
            .WithEstabelecimentos(
                ReceitaReleaseBuilder.Estabelecimento("11222333"),
                // Digito verificador que nao fecha.
                ReceitaReleaseBuilder.Estabelecimento("11444777", dv: "00"))
            .WithEmpresas(("11222333", "GRUPO VENTO SUL VEICULOS LTDA", "05"));

        var (_, captured, graph) = await LoadAsync();

        Assert.Equal(2, captured.AcceptedRows);
        Assert.Equal(1, graph.Rejected);

        Assert.Equal("invalid_cnpj", await ScalarAsync<string>(
            "select rejection_reason from companies_raw where status = 'rejected'"));
    }
}
