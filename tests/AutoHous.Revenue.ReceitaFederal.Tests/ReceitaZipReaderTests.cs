using System.IO.Compression;
using System.Text;

namespace AutoHous.Revenue.ReceitaFederal.Tests;

public class ReceitaZipReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("receita-zip").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Monta um zip no formato da Receita: uma entrada, nome opaco, ISO-8859-1.
    /// </summary>
    private string WriteZip(string csv, string entryName = "F.K03200$Z.D60808.ESTABELE")
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.zip");

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        using var stream = archive.CreateEntry(entryName).Open();
        stream.Write(Encoding.Latin1.GetBytes(csv));

        return path;
    }

    private static async Task<List<string[]>> ReadAsync(string path)
    {
        var rows = new List<string[]>();

        await foreach (var row in ReceitaZipReader.ReadRowsAsync(path, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task Le_a_entrada_unica_apesar_do_nome_opaco()
    {
        // O nome da entrada muda a cada release (F.K03200$Z.D60808.*). O que e
        // estavel e a estrutura: um zip, um arquivo.
        var path = WriteZip("\"11222333\";\"0001\";\"81\"\n");

        var rows = await ReadAsync(path);

        Assert.Equal(["11222333", "0001", "81"], Assert.Single(rows));
    }

    [Fact]
    public async Task Decodifica_iso_8859_1()
    {
        // Ler como UTF-8 nao falha: transforma "Comercio" em texto com caractere
        // de substituicao, e a razao social corrompida so aparece depois.
        var path = WriteZip("\"4511101\";\"Comércio a varejo de automóveis\"\n");

        var rows = await ReadAsync(path);

        Assert.Equal("Comércio a varejo de automóveis", rows[0][1]);
    }

    [Fact]
    public async Task Ignora_linha_em_branco()
    {
        var path = WriteZip("\"a\";\"b\"\n\n\"c\";\"d\"\n");

        Assert.Equal(2, (await ReadAsync(path)).Count);
    }

    [Fact]
    public async Task Zip_com_mais_de_um_arquivo_falha_alto()
    {
        // Nunca aconteceu; se acontecer, significa que o formato mudou. Escolher
        // a maior entrada em silencio esconderia a mudanca.
        var path = Path.Combine(_dir, "duplo.zip");

        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var name in (string[])["A.CSV", "B.CSV"])
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.Latin1.GetBytes("\"x\";\"y\"\n"));
            }
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(path));

        Assert.Contains("esperado 1", error.Message);
        Assert.Contains("A.CSV", error.Message);
    }

    [Fact]
    public async Task Zip_sem_dados_falha_com_o_caminho_no_erro()
    {
        var path = WriteZip(string.Empty);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(path));

        Assert.Contains(path, error.Message);
    }

    [Fact]
    public async Task Razao_social_com_ponto_e_virgula_entre_aspas_continua_um_campo()
    {
        var path = WriteZip("\"11222333\";\"SILVA; SANTOS E CIA LTDA\";\"SP\"\n");

        var row = Assert.Single(await ReadAsync(path));

        Assert.Equal(3, row.Length);
        Assert.Equal("SILVA; SANTOS E CIA LTDA", row[1]);
    }
}
