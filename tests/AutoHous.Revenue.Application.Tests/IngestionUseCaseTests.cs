using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

public class IngestCompanyBatchUseCaseTests
{
    private readonly FakeIngestionBatchRepository _batches = new();
    private readonly FakeUnitOfWorkFactory _uow = new();
    private readonly SequentialIdGenerator _ids = new();

    private IngestCompanyBatchUseCase Subject => new(
        _batches, _uow, _ids, NullLogger<IngestCompanyBatchUseCase>.Instance);

    private static RawCompanyRow Row(string cnpj, string razao = "Grupo Vento Sul Veiculos Ltda") => new()
    {
        Cnpj = cnpj,
        RazaoSocial = razao,
        CnaePrincipal = "4511-1/01",
        SituacaoCadastral = "02",
        Municipio = "Bauru",
        Uf = "SP"
    };

    [Fact]
    public async Task Captura_grava_todas_as_linhas_distintas()
    {
        var result = await Subject.ExecuteAsync(new IngestCompanyBatchCommand
        {
            SourceName = "receita-2026-08",
            Rows = [Row("11222333000181"), Row("11444777000161", "Auto Norte Comercio de Veiculos")]
        });

        Assert.Equal(2, result.TotalRows);
        Assert.Equal(2, result.AcceptedRows);
        Assert.Equal(0, result.DuplicateRows);
        Assert.Equal(1, _uow.CommitCount);
    }

    /// <summary>
    /// A captura nao interpreta: CNPJ invalido e CNAE fora do universo entram
    /// como linha crua. Filtrar aqui destruiria a origem antes de qualquer
    /// chance de corrigir a regra.
    /// </summary>
    [Fact]
    public async Task Captura_nao_julga_o_conteudo_da_linha()
    {
        var result = await Subject.ExecuteAsync(new IngestCompanyBatchCommand
        {
            SourceName = "lixo",
            Rows =
            [
                new RawCompanyRow { Cnpj = "00000000000000", RazaoSocial = "Nao Existe" },
                new RawCompanyRow { Cnpj = null, RazaoSocial = null }
            ]
        });

        Assert.Equal(2, result.AcceptedRows);
    }

    [Fact]
    public async Task Linha_repetida_no_mesmo_arquivo_conta_como_duplicada()
    {
        var result = await Subject.ExecuteAsync(new IngestCompanyBatchCommand
        {
            SourceName = "com-repeticao",
            Rows = [Row("11222333000181"), Row("11222333000181"), Row("11444777000161", "Auto Norte")]
        });

        Assert.Equal(3, result.TotalRows);
        Assert.Equal(2, result.AcceptedRows);
        Assert.Equal(1, result.DuplicateRows);
    }

    /// <summary>Diferenca de caixa e espaco nao cria linha nova.</summary>
    [Fact]
    public async Task Hash_ignora_caixa_e_espacos_ao_redor()
    {
        var result = await Subject.ExecuteAsync(new IngestCompanyBatchCommand
        {
            SourceName = "variacoes",
            Rows =
            [
                Row("11222333000181", "Grupo Vento Sul"),
                Row("11222333000181", "  GRUPO VENTO SUL  ")
            ]
        });

        Assert.Equal(1, result.AcceptedRows);
        Assert.Equal(1, result.DuplicateRows);
    }
}

public class ResolveAccountGraphUseCaseTests
{
    private readonly FakeIngestionBatchRepository _batches = new();
    private readonly FakeAccountGraphRepository _graph = new();
    private readonly FakeUnitOfWorkFactory _uow = new();
    private readonly SequentialIdGenerator _ids = new();

    private ResolveAccountGraphUseCase Subject => new(
        _batches, _graph, _uow, _ids, NullLogger<ResolveAccountGraphUseCase>.Instance);

    private static RawCompanyRow Dealer(string cnpj, string razao, string uf = "SP") => new()
    {
        Cnpj = cnpj,
        RazaoSocial = razao,
        CnaePrincipal = "4511-1/01",
        SituacaoCadastral = "02",
        Municipio = "Bauru",
        Uf = uf
    };

    [Fact]
    public async Task Empresa_sem_candidato_vira_conta_nova()
    {
        _batches.AddPending(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));

        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(1, result.CreatedAccounts);
        Assert.Equal(0, result.AttachedCnpjs);
        Assert.Equal(0, result.ReviewCandidates);
        Assert.Single(_graph.Created);
        Assert.Equal(1m, result.AutoResolvedRate);
    }

    [Fact]
    public async Task Cnae_fora_do_universo_e_rejeitado_com_motivo()
    {
        var raw = _batches.AddPending(new RawCompanyRow
        {
            Cnpj = "11222333000181",
            RazaoSocial = "Padaria do Joao",
            CnaePrincipal = "1091-1/02",
            Uf = "SP"
        });

        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(1, result.Rejected);
        Assert.Empty(_graph.Created);

        var mark = Assert.Single(_batches.Marks);
        Assert.Equal(raw.Id, mark.RawId);
        Assert.Equal(RawCompanyStatus.Rejected, mark.Status);
        Assert.Equal("outside_universe", mark.Reason);
    }

    /// <summary>
    /// Codigo ilegivel e codigo valido fora do universo sao coisas diferentes: o
    /// primeiro e defeito de parsing do arquivo e merece investigacao, o segundo
    /// e o filtro funcionando. Colapsar os dois em um motivo so esconderia uma
    /// coluna mal mapeada atras de "nao e nosso ICP".
    /// </summary>
    [Fact]
    public async Task Cnae_ilegivel_e_distinguido_de_cnae_fora_do_universo()
    {
        _batches.AddPending(new RawCompanyRow
        {
            Cnpj = "11222333000181",
            RazaoSocial = "Coluna Trocada",
            CnaePrincipal = "SP",
            Uf = "SP"
        });

        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(1, result.Rejected);
        Assert.Equal("unknown_cnae", Assert.Single(_batches.Marks).Reason);
    }

    /// <summary>
    /// Filial e matriz compartilham os oito primeiros digitos por definicao da
    /// Receita: nao ha julgamento, e a segunda entra na conta da primeira.
    /// </summary>
    [Fact]
    public async Task Mesma_raiz_de_cnpj_anexa_sem_revisao()
    {
        var existing = Guid.NewGuid();

        _graph.Candidates.Add(new AccountGroupCandidate
        {
            AccountId = existing,
            Name = "Grupo Vento Sul",
            NormalizedName = "GRUPO VENTO SUL VEICULOS",
            Uf = "SP",
            CnpjRoots = ["11222333"],
            NameSimilarity = 0.4m
        });

        _batches.AddPending(Dealer("11222333000262", "Grupo Vento Sul Veiculos Filial Ltda"));

        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(1, result.AttachedCnpjs);
        Assert.Equal(0, result.CreatedAccounts);
        Assert.Equal(existing, Assert.Single(_graph.Attached).AccountId);
    }

    [Fact]
    public async Task Nome_parecido_em_outra_uf_vai_para_revisao()
    {
        _graph.Candidates.Add(new AccountGroupCandidate
        {
            AccountId = Guid.NewGuid(),
            Name = "Vento Sul Veiculos",
            NormalizedName = "VENTO SUL VEICULOS",
            Uf = "RS",
            CnpjRoots = ["99888777"],
            NameSimilarity = 0.95m
        });

        _batches.AddPending(Dealer("11222333000181", "Vento Sul Veiculos Ltda", uf: "SP"));

        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(1, result.ReviewCandidates);
        Assert.Empty(_graph.Created);
        Assert.Empty(_graph.Attached);

        var recorded = Assert.Single(_graph.Recorded);
        Assert.Equal("name_match_other_uf", recorded.Reason);

        // A linha fica em review, nao normalizada: ela ainda nao pertence a
        // nenhuma conta.
        Assert.Equal(RawCompanyStatus.Review, Assert.Single(_batches.Marks).Status);
    }

    /// <summary>
    /// A recarga mensal reencontra CNPJs que ja estao na base. Nao pode duplicar
    /// conta — mas tambem nao pode ser no-op: situacao cadastral, nome fantasia e
    /// municipio mudam, e a Receita e a autoridade sobre eles.
    /// </summary>
    [Fact]
    public async Task Cnpj_ja_conhecido_refresca_o_cadastro_sem_duplicar_conta()
    {
        var existing = Guid.NewGuid();
        _graph.KnownCnpjs["11222333000181"] = existing;

        _batches.AddPending(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));

        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(1, result.AttachedCnpjs);
        Assert.Empty(_graph.Created);
        Assert.Equal(existing, Assert.Single(_graph.Attached).AccountId);
        Assert.Equal(existing, Assert.Single(_batches.Marks).AccountId);
    }

    [Fact]
    public async Task Registra_os_totais_no_lote()
    {
        _batches.AddPending(Dealer("11222333000181", "Grupo Vento Sul Veiculos Ltda"));
        _batches.AddPending(new RawCompanyRow { Cnpj = "invalido", RazaoSocial = "X", CnaePrincipal = "4511-1/01" });

        var batchId = Guid.NewGuid();
        await Subject.ExecuteAsync(batchId);

        Assert.Equal((1, 1, 0, 0), _batches.Resolution);
    }
}

public class DecideMergeCandidateUseCaseTests
{
    private readonly FakeAccountGraphRepository _graph = new();
    private readonly FakeIngestionBatchRepository _batches = new();
    private readonly FakeUnitOfWorkFactory _uow = new();
    private readonly SequentialIdGenerator _ids = new();

    private DecideMergeCandidateUseCase Subject => new(
        _graph, _batches, _uow, _ids, NullLogger<DecideMergeCandidateUseCase>.Instance);

    private (Guid CandidateId, Guid AccountId) GivePendingCandidate()
    {
        var raw = _batches.AddPending(new RawCompanyRow
        {
            Cnpj = "11222333000181",
            RazaoSocial = "Vento Sul Veiculos Ltda",
            CnaePrincipal = "4511-1/01",
            SituacaoCadastral = "02",
            Municipio = "Bauru",
            Uf = "SP"
        });

        var candidateId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        _graph.Pending[candidateId] = new MergeCandidateView
        {
            Id = candidateId,
            AccountId = accountId,
            AccountName = "Vento Sul Veiculos",
            RawId = raw.Id,
            IncomingCnpj = "11222333000181",
            IncomingName = "Vento Sul Veiculos",
            Similarity = 0.95m,
            Reason = "name_match_other_uf",
            Status = "pending"
        };

        return (candidateId, accountId);
    }

    [Fact]
    public async Task Aprovar_anexa_o_cnpj_a_conta_existente()
    {
        var (candidateId, accountId) = GivePendingCandidate();

        var outcome = await Subject.ExecuteAsync(candidateId, approve: true, "pedro");

        Assert.Equal(MergeDecisionOutcome.Merged, outcome);
        Assert.Equal(accountId, Assert.Single(_graph.Attached).AccountId);
        Assert.Empty(_graph.Created);
        Assert.Equal((candidateId, true), Assert.Single(_graph.Decisions));
    }

    /// <summary>
    /// Rejeitar significa "e outra empresa" — e outra empresa merece uma conta.
    /// Sem isso, a linha revisada e negada desapareceria do funil depois de ter
    /// custado revisao humana.
    /// </summary>
    [Fact]
    public async Task Rejeitar_cria_conta_propria_em_vez_de_descartar()
    {
        var (candidateId, _) = GivePendingCandidate();

        var outcome = await Subject.ExecuteAsync(candidateId, approve: false, "pedro");

        Assert.Equal(MergeDecisionOutcome.Rejected, outcome);
        Assert.Single(_graph.Created);
        Assert.Empty(_graph.Attached);

        var mark = Assert.Single(_batches.Marks);
        Assert.Equal(RawCompanyStatus.Normalized, mark.Status);
        Assert.NotNull(mark.AccountId);
    }

    [Fact]
    public async Task Candidato_inexistente_devolve_not_found()
    {
        var outcome = await Subject.ExecuteAsync(Guid.NewGuid(), approve: true, "pedro");

        Assert.Equal(MergeDecisionOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task Candidato_ja_decidido_nao_e_redecidido()
    {
        var (candidateId, _) = GivePendingCandidate();
        _graph.Pending[candidateId] = _graph.Pending[candidateId] with { Status = "approved" };

        var outcome = await Subject.ExecuteAsync(candidateId, approve: true, "pedro");

        Assert.Equal(MergeDecisionOutcome.AlreadyDecided, outcome);
        Assert.Empty(_graph.Decisions);
        Assert.Equal(0, _uow.CommitCount);
    }
}
