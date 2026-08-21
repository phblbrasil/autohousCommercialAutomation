using AutoHous.Revenue.Application;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// Pipeline de captura contra Postgres real: lote -> companies_raw -> account
/// graph -> fila de revisao.
///
/// O que estes testes cobrem e o que os testes de caso de uso nao alcancam: a
/// busca por trigrama de verdade, o indice unico de deduplicacao, e o
/// <c>ON CONFLICT</c> sobre indice parcial da fila de revisao.
/// </summary>
public class IngestionPipelineTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _services = _postgres.BuildWorkerServices();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RawCompanyRow Dealer(
        string cnpj, string razao, string uf = "SP", string cnae = "4511-1/01") => new()
        {
            Cnpj = cnpj,
            RazaoSocial = razao,
            CnaePrincipal = cnae,
            SituacaoCadastral = "02",
            Municipio = "Bauru",
            Uf = uf
        };

    private async Task<Guid> IngestAsync(params RawCompanyRow[] rows)
    {
        var result = await Get<IngestCompanyBatchUseCase>().ExecuteAsync(new IngestCompanyBatchCommand
        {
            SourceName = "teste",
            Rows = rows
        }, Ct);

        return result.BatchId;
    }

    [Fact]
    public async Task Lote_captura_linhas_cruas_e_registra_totais()
    {
        var batchId = await IngestAsync(
            Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"),
            Dealer("11444777000161", "Auto Norte Comercio de Veiculos Ltda"));

        var summary = await Get<IIngestionBatchRepository>().GetAsync(batchId, Ct);

        Assert.NotNull(summary);
        Assert.Equal(IngestionBatchStatus.Captured, summary.Status);
        Assert.Equal(2, summary.TotalRows);
        Assert.Equal(2, summary.AcceptedRows);

        var raw = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from companies_raw where batch_id = @Id", new { Id = batchId });

        Assert.Equal(2, raw);
    }

    [Fact]
    public async Task Resolucao_cria_conta_com_segmento_derivado_do_cnae()
    {
        var batchId = await IngestAsync(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));

        var result = await Get<ResolveAccountGraphUseCase>().ExecuteAsync(batchId, Ct);

        Assert.Equal(1, result.CreatedAccounts);

        var segment = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select segment from accounts where normalized_name = 'GRUPO VENTO SUL VEICULOS'");

        Assert.Equal("concessionaria", segment);

        // O sufixo societario sai do nome de exibicao e fica na razao social.
        var name = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select name from accounts where normalized_name = 'GRUPO VENTO SUL VEICULOS'");

        Assert.Equal("Grupo Vento Sul Veiculos", name);
    }

    /// <summary>
    /// Matriz e filial compartilham a raiz de CNPJ: uma conta, dois CNPJs. E o
    /// principio "Account > CNPJ" funcionando contra o banco.
    /// </summary>
    [Fact]
    public async Task Filial_entra_na_conta_da_matriz()
    {
        var primeiro = await IngestAsync(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));
        await Get<ResolveAccountGraphUseCase>().ExecuteAsync(primeiro, Ct);

        var segundo = await IngestAsync(Dealer("11222333000262", "Grupo Vento Sul Veiculos Ltda"));
        var result = await Get<ResolveAccountGraphUseCase>().ExecuteAsync(segundo, Ct);

        Assert.Equal(1, result.AttachedCnpjs);
        Assert.Equal(0, result.CreatedAccounts);

        var accounts = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from accounts");
        var cnpjs = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from companies_cnpj");

        Assert.Equal(1, accounts);
        Assert.Equal(2, cnpjs);
    }

    /// <summary>
    /// Reprocessar o mesmo lote nao pode duplicar conta nem reencher a fila de
    /// revisao. E o cenario real: a carga mensal traz de novo tudo que ja existe.
    /// </summary>
    [Fact]
    public async Task Reprocessar_o_mesmo_lote_e_idempotente()
    {
        var batchId = await IngestAsync(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));

        await Get<ResolveAccountGraphUseCase>().ExecuteAsync(batchId, Ct);
        await Get<ResolveAccountGraphUseCase>().ExecuteAsync(batchId, Ct);

        var accounts = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from accounts");

        Assert.Equal(1, accounts);
    }

    /// <summary>
    /// Um segundo lote com o mesmo CNPJ tambem nao duplica: a checagem por CNPJ
    /// conhecido acontece antes da busca por similaridade.
    /// </summary>
    [Fact]
    public async Task Cnpj_ja_conhecido_em_outro_lote_nao_cria_conta_nova()
    {
        var primeiro = await IngestAsync(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));
        await Get<ResolveAccountGraphUseCase>().ExecuteAsync(primeiro, Ct);

        var segundo = await IngestAsync(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));
        var result = await Get<ResolveAccountGraphUseCase>().ExecuteAsync(segundo, Ct);

        Assert.Equal(0, result.CreatedAccounts);
        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from accounts"));
    }

    [Fact]
    public async Task Linha_fora_do_universo_e_rejeitada_com_motivo_registrado()
    {
        var batchId = await IngestAsync(
            Dealer("11222333000181", "Padaria Central Ltda", cnae: "1091-1/02"),
            Dealer("11444777000161", "Auto Norte Veiculos Ltda"));

        var result = await Get<ResolveAccountGraphUseCase>().ExecuteAsync(batchId, Ct);

        Assert.Equal(1, result.Rejected);
        Assert.Equal(1, result.CreatedAccounts);

        var reason = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select rejection_reason from companies_raw where status = 'rejected'");

        Assert.Equal("outside_universe", reason);
    }

    /// <summary>
    /// Nome quase identico em outra UF: a busca por trigrama do Postgres traz o
    /// candidato, o resolver manda para revisao, e a conta NAO nasce.
    /// </summary>
    [Fact]
    public async Task Nome_parecido_em_outra_uf_entra_na_fila_de_revisao()
    {
        var primeiro = await IngestAsync(Dealer("11222333000181", "Vento Sul Veiculos Ltda", uf: "SP"));
        await Get<ResolveAccountGraphUseCase>().ExecuteAsync(primeiro, Ct);

        var segundo = await IngestAsync(Dealer("11444777000161", "Vento Sul Veiculos Ltda", uf: "RS"));
        var result = await Get<ResolveAccountGraphUseCase>().ExecuteAsync(segundo, Ct);

        Assert.Equal(1, result.ReviewCandidates);
        Assert.Equal(0, result.CreatedAccounts);

        var pending = await Get<IAccountGraphRepository>().ListPendingCandidatesAsync(10, Ct);
        var candidate = Assert.Single(pending);

        Assert.Equal("11444777000161", candidate.IncomingCnpj);
        Assert.Equal("auto", candidate.Band);
        Assert.Equal("name_match_other_uf", candidate.Reason);
    }

    [Fact]
    public async Task Aprovar_merge_une_os_cnpjs_na_mesma_conta()
    {
        await ArrangeReviewQueueAsync();

        var candidate = (await Get<IAccountGraphRepository>().ListPendingCandidatesAsync(10, Ct)).Single();

        var outcome = await Get<DecideMergeCandidateUseCase>()
            .ExecuteAsync(candidate.Id, approve: true, "pedro", Ct);

        Assert.Equal(MergeDecisionOutcome.Merged, outcome);

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from accounts"));
        Assert.Equal(2, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from companies_cnpj where account_id = @Id", new { Id = candidate.AccountId }));

        Assert.Empty(await Get<IAccountGraphRepository>().ListPendingCandidatesAsync(10, Ct));
    }

    [Fact]
    public async Task Rejeitar_merge_cria_conta_propria()
    {
        await ArrangeReviewQueueAsync();

        var candidate = (await Get<IAccountGraphRepository>().ListPendingCandidatesAsync(10, Ct)).Single();

        var outcome = await Get<DecideMergeCandidateUseCase>()
            .ExecuteAsync(candidate.Id, approve: false, "pedro", Ct);

        Assert.Equal(MergeDecisionOutcome.Rejected, outcome);

        // A empresa negada nao some do funil: vira conta distinta.
        Assert.Equal(2, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from accounts"));

        var status = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select status from companies_raw where cnpj_raw = '11444777000161'");

        Assert.Equal(RawCompanyStatus.Normalized, status);
    }

    [Fact]
    public async Task Candidato_ja_decidido_nao_aceita_segunda_decisao()
    {
        await ArrangeReviewQueueAsync();

        var candidate = (await Get<IAccountGraphRepository>().ListPendingCandidatesAsync(10, Ct)).Single();
        var decide = Get<DecideMergeCandidateUseCase>();

        await decide.ExecuteAsync(candidate.Id, approve: true, "pedro", Ct);
        var second = await decide.ExecuteAsync(candidate.Id, approve: false, "pedro", Ct);

        Assert.Equal(MergeDecisionOutcome.AlreadyDecided, second);
    }

    private async Task ArrangeReviewQueueAsync()
    {
        var primeiro = await IngestAsync(Dealer("11222333000181", "Vento Sul Veiculos Ltda", uf: "SP"));
        await Get<ResolveAccountGraphUseCase>().ExecuteAsync(primeiro, Ct);

        var segundo = await IngestAsync(Dealer("11444777000161", "Vento Sul Veiculos Ltda", uf: "RS"));
        await Get<ResolveAccountGraphUseCase>().ExecuteAsync(segundo, Ct);
    }
}
