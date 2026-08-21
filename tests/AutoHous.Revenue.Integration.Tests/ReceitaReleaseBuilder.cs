using System.IO.Compression;
using System.Text;
using AutoHous.Revenue.Application;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// Monta um release sintetico no formato exato da Receita: zip com uma entrada
/// de nome opaco, CSV sem cabecalho, delimitado por ponto e virgula, entre aspas
/// e em ISO-8859-1.
///
/// Escrever os zips de verdade — em vez de simular o leitor — e o ponto: o
/// caminho testado passa pelo <c>ZipArchive</c>, pela decodificacao latin1 e
/// pelo mapeamento posicional. Um layout lido errado continua produzindo dado com
/// cara de valido, e so um arquivo real pega isso.
/// </summary>
public sealed class ReceitaReleaseBuilder(string cacheDirectory, string release)
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(cacheDirectory, release)).FullName;

    public string Release => release;

    /// <summary>Nome opaco, no padrao que a Receita usa e que muda a cada carga.</summary>
    private static string EntryName(string suffix) => $"F.K03200$Z.D60808.{suffix}";

    private void Write(string fileName, string entrySuffix, IEnumerable<string[]> rows)
    {
        using var file = File.Create(Path.Combine(_dir, fileName));
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry(EntryName(entrySuffix)).Open();

        var csv = new StringBuilder();

        foreach (var row in rows)
        {
            csv.AppendLine(string.Join(';', row.Select(f => $"\"{f}\"")));
        }

        stream.Write(Encoding.Latin1.GetBytes(csv.ToString()));
    }

    public ReceitaReleaseBuilder WithDomainTables()
    {
        Write("Cnaes.zip", "CNAECSV", [
            ["4511101", "Comércio a varejo de automóveis, camionetas e utilitários novos"],
            ["4511102", "Comércio a varejo de automóveis, camionetas e utilitários usados"],
            ["1091102", "Fabricação de produtos de padaria e confeitaria"]
        ]);

        Write("Municipios.zip", "MUNICCSV", [["7107", "BAURU"], ["8801", "PORTO ALEGRE"]]);
        Write("Naturezas.zip", "NATJUCSV", [["2062", "Sociedade Empresária Limitada"]]);
        Write("Motivos.zip", "MOTICSV", [["00", "SEM MOTIVO"], ["01", "EXTINCAO POR ENCERRAMENTO LIQUIDACAO VOLUNTARIA"]]);
        Write("Paises.zip", "PAISCSV", [["105", "BRASIL"]]);

        return this;
    }

    /// <summary>
    /// Uma linha de estabelecimento com os 30 campos do layout oficial nas
    /// posicoes certas.
    /// </summary>
    public static string[] Estabelecimento(
        string cnpjBasico,
        string ordem = "0001",
        string dv = "81",
        string matrizFilial = "1",
        string fantasia = "VENTO SUL",
        string situacao = "02",
        string cnae = "4511101",
        string uf = "SP",
        string municipio = "7107",
        string logradouro = "DAS PALMEIRAS",
        string numero = "120",
        string cnaesSecundarios = "")
    {
        var f = new string[30];
        Array.Fill(f, string.Empty);

        f[0] = cnpjBasico;
        f[1] = ordem;
        f[2] = dv;
        f[3] = matrizFilial;
        f[4] = fantasia;
        f[5] = situacao;
        f[6] = "20240115";
        f[7] = "00";
        f[10] = "20100312";
        f[11] = cnae;
        f[12] = cnaesSecundarios;
        f[13] = "RUA";
        f[14] = logradouro;
        f[15] = numero;
        f[17] = "CENTRO";
        f[18] = "17010000";
        f[19] = uf;
        f[20] = municipio;
        f[21] = "14";
        f[22] = "32345678";
        f[27] = "contato@ventosul.com.br";

        return f;
    }

    public ReceitaReleaseBuilder WithEstabelecimentos(params string[][] rows)
    {
        Write("Estabelecimentos0.zip", "ESTABELE", rows);
        return this;
    }

    public ReceitaReleaseBuilder WithEmpresas(params (string Basico, string Razao, string Porte)[] rows)
    {
        Write("Empresas0.zip", "EMPRECSV",
            rows.Select(r => new[] { r.Basico, r.Razao, "2062", "49", "1250000,50", r.Porte, string.Empty }));

        return this;
    }

    public ReceitaReleaseBuilder WithSimples(params (string Basico, string Simples, string Mei)[] rows)
    {
        Write("Simples.zip", "SIMPLES",
            rows.Select(r => new[]
            {
                r.Basico, r.Simples, "20150101", "00000000", r.Mei, "00000000", "00000000"
            }));

        return this;
    }

    public ReceitaReleaseBuilder WithSocios(params (string Basico, string Nome, string Cpf)[] rows)
    {
        Write("Socios0.zip", "SOCIOCSV",
            rows.Select(r => new[]
            {
                r.Basico, "2", r.Nome, r.Cpf, "49", "20100312", string.Empty,
                string.Empty, string.Empty, string.Empty, "5"
            }));

        return this;
    }

    /// <summary>
    /// Origem que serve os arquivos ja gravados no cache. O tamanho declarado
    /// bate com o do disco, entao o <c>ReceitaFileCache</c> considera tudo
    /// integro e nao baixa nada — o teste exercita a leitura, nao a rede.
    /// </summary>
    public IReceitaFederalArchive AsArchive() => new LocalArchive(_dir, release);

    private sealed class LocalArchive(string directory, string release) : IReceitaFederalArchive
    {
        public Task<IReadOnlyList<string>> ListReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([release]);

        public Task<IReadOnlyList<ReceitaArchiveFile>> ListFilesAsync(
            string release, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReceitaArchiveFile>>(
            [
                .. new DirectoryInfo(directory)
                    .EnumerateFiles("*.zip")
                    .Select(f => new ReceitaArchiveFile(f.Name, f.Length, f.LastWriteTimeUtc))
            ]);

        public Task<Stream> OpenAsync(
            string release, string fileName, long offset, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                $"O cache deveria ter considerado '{fileName}' integro e nao baixar nada.");
    }
}
