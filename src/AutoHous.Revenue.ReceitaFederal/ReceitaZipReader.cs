using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>
/// Le, em stream, o CSV que mora dentro de um zip da Receita.
///
/// Duas particularidades da fonte que o codigo tem de absorver:
///
/// 1. A entrada tem nome opaco e mutavel - <c>F.K03200$Z.D60808.CNAECSV</c> -,
///    entao abrir por nome nao funciona. O que e estavel e a estrutura: um zip,
///    um arquivo.
/// 2. O conteudo e ISO-8859-1, nao UTF-8. Ler como UTF-8 nao falha: transforma
///    "Comercio" em texto com caractere de substituicao, e a razao social
///    corrompida so aparece semanas depois, num e-mail para o cliente.
/// </summary>
public static class ReceitaZipReader
{
    /// <summary>
    /// Buffer generoso: sao dezenas de milhoes de linhas, e o padrao de 1 KB
    /// multiplicaria as chamadas de leitura sem necessidade.
    /// </summary>
    private const int BufferSize = 1 << 16;

    public static async IAsyncEnumerable<string[]> ReadRowsAsync(
        string zipPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var entry = SingleEntry(archive, zipPath);

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.Latin1, false, BufferSize);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;

            yield return QuotedDelimitedLine.Split(line, ';');
        }
    }

    private static ZipArchiveEntry SingleEntry(ZipArchive archive, string zipPath)
    {
        var candidates = archive.Entries
            .Where(e => e.Length > 0 && !e.FullName.EndsWith('/'))
            .ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidDataException($"Zip da Receita sem arquivo de dados: {zipPath}"),
            // Mais de uma entrada nunca aconteceu, e se acontecer significa que o
            // formato mudou. Escolher a maior calado esconderia a mudanca.
            _ => throw new InvalidDataException(
                $"Zip da Receita com {candidates.Count} arquivos, esperado 1: {zipPath}. " +
                $"Entradas: {string.Join(", ", candidates.Select(e => e.FullName))}")
        };
    }
}
