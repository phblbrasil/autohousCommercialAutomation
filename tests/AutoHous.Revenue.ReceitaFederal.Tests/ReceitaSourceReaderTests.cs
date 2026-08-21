using System.IO.Compression;
using System.Text;
using AutoHous.Revenue.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.ReceitaFederal.Tests;

public class ReceitaSourceReaderTests : IDisposable
{
    private const string Release = "2026-08";

    private readonly string _root = Directory.CreateTempSubdirectory("receita-source").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CacheDir => Path.Combine(_root, "cache");

    private void WriteZip(string fileName, params string[][] rows)
    {
        var dir = Directory.CreateDirectory(Path.Combine(CacheDir, Release));

        using var file = File.Create(Path.Combine(dir.FullName, fileName));
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry("F.K03200$Z.D60808.OPACO").Open();

        var csv = string.Join('\n', rows.Select(r => string.Join(';', r.Select(f => $"\"{f}\"")))) + "\n";
        stream.Write(Encoding.Latin1.GetBytes(csv));
    }

    private void WriteDomainTables()
    {
        WriteZip("Cnaes.zip", ["4511101", "Comércio a varejo de automóveis"]);
        WriteZip("Municipios.zip", ["7107", "BAURU"], ["0275", "SAO PAULO DE OLIVENCA"]);
        WriteZip("Naturezas.zip", ["2062", "Sociedade Empresária Limitada"]);
        WriteZip("Motivos.zip", ["00", "SEM MOTIVO"]);
        WriteZip("Paises.zip", ["105", "BRASIL"]);
    }

    private static string[] Estabelecimento(string basico, string cnae = "4511101")
    {
        var f = new string[30];
        Array.Fill(f, string.Empty);
        f[0] = basico;
        f[1] = "0001";
        f[2] = "81";
        f[3] = "1";
        f[5] = "02";
        f[11] = cnae;
        f[19] = "SP";
        f[20] = "7107";
        return f;
    }

    /// <summary>Origem que nunca deve ser consultada: o modo offline nao fala com a rede.</summary>
    private sealed class ForbiddenArchive : IReceitaFederalArchive
    {
        public Task<IReadOnlyList<string>> ListReleasesAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("modo offline nao pode consultar a origem");

        public Task<IReadOnlyList<ReceitaArchiveFile>> ListFilesAsync(string release, CancellationToken ct = default) =>
            throw new InvalidOperationException("modo offline nao pode consultar a origem");

        public Task<Stream> OpenAsync(string release, string fileName, long offset, CancellationToken ct = default) =>
            throw new InvalidOperationException("modo offline nao pode baixar nada");
    }

    private ReceitaSourceReader BuildOffline()
    {
        var archive = new ForbiddenArchive();

        var options = Options.Create(new ReceitaOptions
        {
            CacheDirectory = CacheDir,
            OfflineOnly = true
        });

        var cache = new ReceitaFileCache(archive, options, NullLogger<ReceitaFileCache>.Instance);

        return new ReceitaSourceReader(archive, cache, options, NullLogger<ReceitaSourceReader>.Instance);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Modo_offline_le_o_cache_sem_tocar_na_origem()
    {
        // O caso real: 7,3 GB baixados por outro meio - gerenciador de download,
        // maquina sem saida para a internet, copia que ja circula na equipe.
        WriteDomainTables();
        WriteZip("Estabelecimentos0.zip", Estabelecimento("11222333"));
        WriteZip("Empresas0.zip", ["11222333", "GRUPO VENTO SUL VEICULOS LTDA", "2062", "49", "1000,00", "05", ""]);

        var reader = BuildOffline();

        var digests = await reader.EnsureLocalAsync(Release, ReceitaFileSet.Minimum, Ct);

        Assert.Equal(7, digests.Count);
        Assert.All(digests, d => Assert.Equal(64, d.Sha256.Length));
    }

    [Fact]
    public async Task Cache_incompleto_falha_dizendo_o_que_falta()
    {
        // Carregar sem Empresas produziria centenas de milhares de linhas sem
        // razao social, todas rejeitadas por "missing_name" - com aparencia de
        // problema de dado em vez de arquivo faltando.
        WriteDomainTables();
        WriteZip("Estabelecimentos0.zip", Estabelecimento("11222333"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildOffline().EnsureLocalAsync(Release, ReceitaFileSet.Minimum, Ct));

        Assert.Contains("Empresas", error.Message);
    }

    [Fact]
    public async Task Cache_inexistente_diz_o_caminho_esperado()
    {
        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => BuildOffline().EnsureLocalAsync(Release, ReceitaFileSet.Minimum, Ct));

        Assert.Contains(Release, error.Message);
    }

    [Fact]
    public async Task Fatias_sao_lidas_todas_e_em_ordem()
    {
        // O numero de fatias vem do que existe, e nao de um dez fixo no codigo:
        // uma fatia nova ignorada em silencio e a pior forma de perder dado.
        WriteDomainTables();
        WriteZip("Estabelecimentos0.zip", Estabelecimento("11111111"));
        WriteZip("Estabelecimentos1.zip", Estabelecimento("22222222"));
        WriteZip("Estabelecimentos2.zip", Estabelecimento("33333333"));
        WriteZip("Empresas0.zip", ["11111111", "UM", "2062", "49", "1000,00", "05", ""]);

        var reader = BuildOffline();
        await reader.EnsureLocalAsync(Release, ReceitaFileSet.Minimum, Ct);

        var lidos = new List<string>();

        await foreach (var est in reader.ReadEstabelecimentosAsync(Release, Ct))
        {
            lidos.Add(est.CnpjBasico);
        }

        Assert.Equal(["11111111", "22222222", "33333333"], lidos);
    }

    [Fact]
    public async Task Simples_ausente_nao_derruba_a_carga()
    {
        // Enriquecimento, nao insumo: sem ele a empresa entra sem a opcao pelo
        // Simples/MEI. Trocar dado parcial por dado nenhum seria pior.
        WriteDomainTables();
        WriteZip("Estabelecimentos0.zip", Estabelecimento("11222333"));
        WriteZip("Empresas0.zip", ["11222333", "UM", "2062", "49", "1000,00", "05", ""]);

        var reader = BuildOffline();
        await reader.EnsureLocalAsync(Release, ReceitaFileSet.Default, Ct);

        var simples = new List<ReceitaSimples>();

        await foreach (var row in reader.ReadSimplesAsync(Release, Ct)) simples.Add(row);

        Assert.Empty(simples);
    }

    [Fact]
    public async Task Tabelas_de_dominio_indexam_o_codigo_com_e_sem_zero_a_esquerda()
    {
        // O mesmo municipio circula como "0275" e como "275" conforme a
        // ferramenta. O join nao pode falhar por formatacao.
        WriteDomainTables();
        WriteZip("Estabelecimentos0.zip", Estabelecimento("11222333"));
        WriteZip("Empresas0.zip", ["11222333", "UM", "2062", "49", "1000,00", "05", ""]);

        var reader = BuildOffline();
        await reader.EnsureLocalAsync(Release, ReceitaFileSet.Minimum, Ct);

        var tables = await reader.ReadDomainTablesAsync(Release, Ct);

        Assert.Equal("BAURU", tables.Municipios["7107"]);
        Assert.Equal("SAO PAULO DE OLIVENCA", tables.Municipios["0275"]);
        Assert.Equal("SAO PAULO DE OLIVENCA", tables.Municipios["275"]);
        Assert.Equal("Sociedade Empresária Limitada", tables.Naturezas["2062"]);
    }

    [Fact]
    public async Task Socios_so_sao_exigidos_quando_pedidos()
    {
        WriteDomainTables();
        WriteZip("Estabelecimentos0.zip", Estabelecimento("11222333"));
        WriteZip("Empresas0.zip", ["11222333", "UM", "2062", "49", "1000,00", "05", ""]);

        var reader = BuildOffline();

        // Sem a flag, a ausencia de Socios e irrelevante.
        await reader.EnsureLocalAsync(Release, ReceitaFileSet.Minimum, Ct);

        // Com a flag, o operador pediu PII explicitamente: faltar o arquivo e erro.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.EnsureLocalAsync(Release, ReceitaFileSet.Minimum | ReceitaFileSet.Socios, Ct));

        Assert.Contains("Socios", error.Message);
    }
}
