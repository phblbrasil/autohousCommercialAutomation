using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

public class IngestCompanyStreamUseCaseTests
{
    private static async IAsyncEnumerable<RawCompanyRow> Stream(params RawCompanyRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    private static RawCompanyRow Row(string cnpj) => new()
    {
        Cnpj = cnpj,
        RazaoSocial = "GRUPO VENTO SUL VEICULOS LTDA",
        CnaePrincipal = "4511101",
        SituacaoCadastral = "02",
        Uf = "SP"
    };

    private static (IngestCompanyStreamUseCase UseCase, FakeIngestionBatchRepository Batches, FakeUnitOfWorkFactory Uow) Build()
    {
        var batches = new FakeIngestionBatchRepository();
        var uow = new FakeUnitOfWorkFactory();

        return (
            new IngestCompanyStreamUseCase(
                batches, uow, new SequentialIdGenerator(),
                NullLogger<IngestCompanyStreamUseCase>.Instance),
            batches,
            uow);
    }

    [Fact]
    public async Task Grava_todas_as_linhas_do_stream()
    {
        var (useCase, batches, _) = Build();

        var result = await useCase.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = "receita-2026-08",
            Rows = Stream(Row("11222333000181"), Row("11222333000262"))
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalRows);
        Assert.Equal(2, result.AcceptedRows);
        Assert.Equal(2, batches.Rows.Count);
    }

    [Fact]
    public async Task Numera_as_linhas_na_ordem_de_leitura()
    {
        // O numero da linha e o unico ponteiro de volta para a origem quando
        // alguem precisa entender uma rejeicao.
        var (useCase, batches, _) = Build();

        await useCase.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = "receita",
            Rows = Stream(Row("11222333000181"), Row("11222333000262"), Row("11444777000161"))
        }, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], batches.Rows.Select(r => r.RowNumber));
    }

    [Fact]
    public async Task Uma_transacao_por_bloco_e_nao_uma_para_o_lote()
    {
        // Com centenas de milhares de linhas, transacao unica segura locks por
        // minutos e perde tudo em qualquer erro.
        var (useCase, _, uow) = Build();

        var rows = Enumerable.Range(1, 10).Select(i => Row($"1122233300{i:D4}")).ToArray();

        await useCase.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = "receita",
            Rows = Stream(rows),
            ChunkSize = 3
        }, TestContext.Current.CancellationToken);

        // 1 abertura + 4 blocos (3+3+3+1) + 1 fechamento.
        Assert.Equal(6, uow.CommitCount);
    }

    [Fact]
    public async Task Linha_repetida_no_stream_nao_gasta_insert()
    {
        var (useCase, batches, _) = Build();

        var result = await useCase.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = "receita",
            Rows = Stream(Row("11222333000181"), Row("11222333000181"), Row("11222333000262"))
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.TotalRows);
        Assert.Equal(2, result.AcceptedRows);
        Assert.Equal(1, result.DuplicateRows);
        Assert.Equal(2, batches.Rows.Count);
    }

    [Fact]
    public async Task Stream_vazio_fecha_o_lote_em_vez_de_falhar()
    {
        // Um recorte por UF pode nao selecionar nada. Lote vazio e um resultado,
        // nao uma excecao.
        var (useCase, _, uow) = Build();

        var result = await useCase.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = "receita",
            Rows = Stream()
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalRows);
        Assert.Equal(2, uow.CommitCount);
    }

    [Fact]
    public async Task Respeita_o_id_de_lote_informado_pelo_chamador()
    {
        var (useCase, batches, _) = Build();
        var batchId = Guid.CreateVersion7();

        var result = await useCase.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = "receita",
            BatchId = batchId,
            Rows = Stream(Row("11222333000181"))
        }, TestContext.Current.CancellationToken);

        Assert.Equal(batchId, result.BatchId);
        Assert.Equal(batchId, Assert.Single(batches.Batches).Id);
    }

    [Fact]
    public async Task Bloco_de_tamanho_invalido_falha_antes_de_abrir_o_lote()
    {
        var (useCase, batches, _) = Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => useCase.ExecuteAsync(
            new IngestCompanyStreamCommand
            {
                SourceName = "receita",
                Rows = Stream(Row("11222333000181")),
                ChunkSize = 0
            },
            TestContext.Current.CancellationToken));

        Assert.Empty(batches.Batches);
    }

    [Fact]
    public async Task Campos_da_receita_chegam_intactos_ao_repositorio()
    {
        var (useCase, batches, _) = Build();

        await useCase.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = "receita",
            Rows = Stream(Row("11222333000181") with
            {
                Porte = "05",
                CapitalSocial = "1250000,50",
                Telefone1 = "1432345678",
                Email = "contato@ventosul.com.br",
                OpcaoSimples = "N"
            })
        }, TestContext.Current.CancellationToken);

        var row = Assert.Single(batches.Rows).Row;

        Assert.Equal("05", row.Porte);
        Assert.Equal("1250000,50", row.CapitalSocial);
        Assert.Equal("contato@ventosul.com.br", row.Email);
    }
}
