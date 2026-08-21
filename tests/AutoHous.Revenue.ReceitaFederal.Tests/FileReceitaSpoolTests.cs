using AutoHous.Revenue.Application;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.ReceitaFederal.Tests;

public class FileReceitaSpoolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("receita-spool").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FileReceitaSpool Build() =>
        new(Options.Create(new ReceitaOptions { WorkDirectory = _dir }));

    private static RawCompanyRow Row(string cnpj, string? fantasia = null) => new()
    {
        Cnpj = cnpj,
        NomeFantasia = fantasia,
        CnaePrincipal = "4511101",
        Uf = "SP",
        Municipio = "Bauru"
    };

    private async Task<List<RawCompanyRow>> ReadAsync(FileReceitaSpool spool, string name)
    {
        var rows = new List<RawCompanyRow>();

        await foreach (var row in spool.ReadAsync(name, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task Preserva_conteudo_e_ordem()
    {
        // A ordem e o que faz matriz entrar antes de filial: com a matriz ja na
        // base, a filial anexa por raiz de CNPJ em vez de disputar trigrama.
        var spool = Build();

        await spool.AppendAsync("matriz",
            [Row("11222333000181", "Vento Sul"), Row("11222333000262", "Vento Sul Bauru")],
            TestContext.Current.CancellationToken);

        var rows = await ReadAsync(spool, "matriz");

        Assert.Equal(["11222333000181", "11222333000262"], rows.Select(r => r.Cnpj));
        Assert.Equal("Vento Sul", rows[0].NomeFantasia);
    }

    [Fact]
    public async Task Append_acumula_entre_blocos()
    {
        var spool = Build();

        await spool.AppendAsync("matriz", [Row("11222333000181")], TestContext.Current.CancellationToken);
        await spool.AppendAsync("matriz", [Row("11222333000262")], TestContext.Current.CancellationToken);

        Assert.Equal(2, (await ReadAsync(spool, "matriz")).Count);
    }

    [Fact]
    public async Task Reset_descarta_a_carga_anterior()
    {
        // Recarregar o mesmo release tem que comecar limpo: acumular sobre o
        // spool antigo dobraria as linhas.
        var spool = Build();

        await spool.AppendAsync("matriz", [Row("11222333000181")], TestContext.Current.CancellationToken);
        await spool.ResetAsync("matriz", TestContext.Current.CancellationToken);

        Assert.Empty(await ReadAsync(spool, "matriz"));
    }

    [Fact]
    public async Task Spool_inexistente_e_sequencia_vazia_e_nao_erro()
    {
        // Um recorte por UF pode nao ter nenhuma matriz. Isso nao e falha.
        Assert.Empty(await ReadAsync(Build(), "filial"));
    }

    [Fact]
    public async Task Campos_da_receita_sobrevivem_a_ida_e_volta()
    {
        var spool = Build();

        await spool.AppendAsync("matriz",
            [Row("11222333000181") with
            {
                Porte = "05",
                CapitalSocial = "1250000,50",
                Telefone1 = "1432345678",
                MatrizFilial = "1",
                CnaesSecundarios = "4520001,4530703"
            }],
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await ReadAsync(spool, "matriz"));

        Assert.Equal("05", row.Porte);
        Assert.Equal("1250000,50", row.CapitalSocial);
        Assert.Equal("4520001,4530703", row.CnaesSecundarios);
        Assert.Equal("1", row.MatrizFilial);
    }
}
