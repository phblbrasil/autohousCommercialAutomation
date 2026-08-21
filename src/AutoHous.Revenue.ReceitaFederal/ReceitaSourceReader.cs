using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AutoHous.Revenue.Application;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>
/// Leitura de um release inteiro: baixa o que falta e entrega as linhas ja
/// tipadas, em stream.
///
/// A Receita parte cada tabela grande em arquivos numerados
/// (<c>Estabelecimentos0..9</c>). Aqui eles voltam a ser uma sequencia unica:
/// quem le nao deveria precisar saber que a fonte foi fatiada para caber no
/// servidor de arquivos dela.
/// </summary>
public sealed class ReceitaSourceReader(
    IReceitaFederalArchive archive,
    ReceitaFileCache cache,
    IOptions<ReceitaOptions> options,
    ILogger<ReceitaSourceReader> logger) : IReceitaSourceReader
{
    /// <summary>Tabelas de dominio: minusculas, e nenhuma delas e fatiada.</summary>
    private static readonly string[] DomainFiles =
        ["Cnaes.zip", "Municipios.zip", "Naturezas.zip", "Motivos.zip", "Paises.zip"];

    private readonly ReceitaOptions _options = options.Value;

    public async Task<IReadOnlyList<ReceitaFileDigest>> EnsureLocalAsync(
        string release, ReceitaFileSet files, CancellationToken ct = default)
    {
        // Offline: o que esta no cache E o release. A validacao de completude
        // continua valendo - Select() derruba a carga se faltar Estabelecimentos
        // ou Empresas -, so a origem dos tamanhos muda.
        var available = _options.OfflineOnly
            ? LocalFiles(release)
            : await archive.ListFilesAsync(release, ct);

        if (available.Count == 0)
        {
            throw new InvalidOperationException(
                $"Release '{release}' nao tem arquivos. Confira as competencias com --list.");
        }

        var targets = Select(available, files, release);

        var total = targets.Sum(t => t.Length);
        logger.LogInformation(
            "Release {Release}: {Count} arquivo(s), {Megabytes:N0} MB a garantir localmente.",
            release, targets.Count, total / 1024d / 1024d);

        var digests = new List<ReceitaFileDigest>(targets.Count);

        // Sequencial de proposito. Paralelizar quatro downloads de 2 GB satura o
        // link e transforma uma falha de rede em quatro arquivos parciais.
        foreach (var target in targets)
        {
            digests.Add(await cache.EnsureAsync(release, target, ct));
        }

        return digests;
    }

    private IReadOnlyList<ReceitaArchiveFile> LocalFiles(string release)
    {
        var directory = new DirectoryInfo(cache.DirectoryFor(release));

        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException(
                $"Modo offline sem cache para {release}: {directory.FullName} nao existe.");
        }

        return
        [
            .. directory
                .EnumerateFiles("*.zip")
                .Select(f => new ReceitaArchiveFile(f.Name, f.Length, f.LastWriteTimeUtc))
        ];
    }

    public async Task<ReceitaDomainTables> ReadDomainTablesAsync(
        string release, CancellationToken ct = default) =>
        new()
        {
            Cnaes = await ReadDomainAsync(release, "Cnaes.zip", ct),
            Municipios = await ReadDomainAsync(release, "Municipios.zip", ct),
            Naturezas = await ReadDomainAsync(release, "Naturezas.zip", ct),
            Motivos = await ReadDomainAsync(release, "Motivos.zip", ct),
            Paises = await ReadDomainAsync(release, "Paises.zip", ct)
        };

    public IAsyncEnumerable<ReceitaEstabelecimento> ReadEstabelecimentosAsync(
        string release, CancellationToken ct = default) =>
        ReadSlicedAsync(release, "Estabelecimentos", ReceitaLayout.ToEstabelecimento, ct);

    public IAsyncEnumerable<ReceitaEmpresa> ReadEmpresasAsync(
        string release, CancellationToken ct = default) =>
        ReadSlicedAsync(release, "Empresas", ReceitaLayout.ToEmpresa, ct);

    public IAsyncEnumerable<ReceitaSimples> ReadSimplesAsync(
        string release, CancellationToken ct = default) =>
        ReadFileAsync(release, "Simples.zip", ReceitaLayout.ToSimples, ct, optional: true);

    public IAsyncEnumerable<ReceitaSocio> ReadSociosAsync(
        string release, CancellationToken ct = default) =>
        ReadSlicedAsync(release, "Socios", ReceitaLayout.ToSocio, ct);

    // ------------------------------------------------------------------ leitura

    private async IAsyncEnumerable<T> ReadSlicedAsync<T>(
        string release,
        string prefix,
        Func<string[], T?> map,
        [EnumeratorCancellation] CancellationToken ct)
        where T : class
    {
        var directory = cache.DirectoryFor(release);
        var pattern = SlicePattern(prefix);

        var slices = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.zip")
                .Where(path => pattern.IsMatch(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
            : [];

        if (slices.Count == 0)
        {
            throw new FileNotFoundException(
                $"Nenhuma fatia de '{prefix}' no cache de {release}. Rode EnsureLocalAsync antes.");
        }

        foreach (var slice in slices)
        {
            await foreach (var row in ReadFileAsync(release, Path.GetFileName(slice), map, ct))
            {
                yield return row;
            }
        }
    }

    private async IAsyncEnumerable<T> ReadFileAsync<T>(
        string release,
        string fileName,
        Func<string[], T?> map,
        [EnumeratorCancellation] CancellationToken ct,
        bool optional = false)
        where T : class
    {
        var path = cache.PathFor(release, fileName);

        if (!File.Exists(path))
        {
            if (!optional)
            {
                throw new FileNotFoundException(
                    $"Arquivo do release nao esta no cache: {path}. Rode EnsureLocalAsync antes.", path);
            }

            logger.LogWarning(
                "{File} nao publicado em {Release}: as empresas entram sem opcao pelo Simples/MEI.",
                fileName, release);

            yield break;
        }

        long read = 0, malformed = 0;

        await foreach (var fields in ReceitaZipReader.ReadRowsAsync(path, ct))
        {
            read++;

            var mapped = map(fields);

            if (mapped is null)
            {
                malformed++;
                continue;
            }

            yield return mapped;
        }

        // Linha sem raiz de CNPJ legivel nao pertence a empresa nenhuma. Contar e
        // reportar em vez de descartar em silencio: se o layout mudar de posicao,
        // este numero e o primeiro sinal - e ele aparece antes de a carga
        // terminar com "faltaram 200 mil empresas" sem explicacao.
        if (malformed > 0)
        {
            logger.LogWarning(
                "{File}: {Malformed} de {Read} linha(s) sem raiz de CNPJ legivel.",
                fileName, malformed, read);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadDomainAsync(
        string release, string fileName, CancellationToken ct)
    {
        var path = cache.PathFor(release, fileName);
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            logger.LogWarning("Tabela de dominio ausente do cache: {File}.", fileName);
            return table;
        }

        await foreach (var fields in ReceitaZipReader.ReadRowsAsync(path, ct))
        {
            if (ReceitaLayout.ToDomainEntry(fields) is not { } entry) continue;

            table[entry.Code] = entry.Description;

            // Municipio e CNAE circulam com e sem zero a esquerda conforme a
            // ferramenta que gerou o extrato. Indexar as duas formas evita que o
            // join falhe por uma diferenca de formatacao.
            var unpadded = entry.Code.TrimStart('0');
            if (unpadded.Length > 0) table.TryAdd(unpadded, entry.Description);
        }

        return table;
    }

    /// <summary>
    /// Escolhe os arquivos do release a partir do que a origem REALMENTE lista,
    /// e nao de uma lista fixa de dez fatias.
    ///
    /// A Receita fatia as tabelas grandes em <c>Estabelecimentos0..9</c> hoje.
    /// Fixar o dez no codigo faria a carga ignorar uma fatia nova em silencio -
    /// a pior forma de perder dado, porque o resultado continua parecendo
    /// completo.
    /// </summary>
    private static List<ReceitaArchiveFile> Select(
        IReadOnlyList<ReceitaArchiveFile> available, ReceitaFileSet files, string release)
    {
        var targets = new List<ReceitaArchiveFile>();

        void TakeExact(string name, bool required = true)
        {
            var found = available.FirstOrDefault(
                f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

            if (found is not null)
            {
                targets.Add(found);
                return;
            }

            if (required) throw new InvalidOperationException($"Release '{release}' nao publica '{name}'.");
        }

        void TakeSlices(string prefix)
        {
            var pattern = SlicePattern(prefix);
            var slices = available.Where(f => pattern.IsMatch(f.Name)).ToList();

            if (slices.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Release '{release}' nao publica nenhuma fatia de '{prefix}'.");
            }

            targets.AddRange(slices);
        }

        if (files.HasFlag(ReceitaFileSet.DomainTables))
        {
            foreach (var file in DomainFiles) TakeExact(file);
        }

        if (files.HasFlag(ReceitaFileSet.Estabelecimentos)) TakeSlices("Estabelecimentos");
        if (files.HasFlag(ReceitaFileSet.Empresas)) TakeSlices("Empresas");
        // Simples e enriquecimento, nao insumo: sem ele a empresa entra na base
        // sem a opcao pelo Simples/MEI. Derrubar a carga inteira por causa de um
        // arquivo opcional seria trocar dado parcial por dado nenhum.
        if (files.HasFlag(ReceitaFileSet.Simples)) TakeExact("Simples.zip", required: false);
        if (files.HasFlag(ReceitaFileSet.Socios)) TakeSlices("Socios");

        // Menores primeiro: um erro de layout aparece depois de 100 KB de tabela
        // de dominio, e nao depois de 2 GB de estabelecimentos.
        return [.. targets.OrderBy(f => f.Length)];
    }

    private static Regex SlicePattern(string prefix) =>
        new($"^{Regex.Escape(prefix)}[0-9]+\\.zip$", RegexOptions.IgnoreCase);
}
