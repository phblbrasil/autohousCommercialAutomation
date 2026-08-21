using System.Security.Cryptography;
using AutoHous.Revenue.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>
/// Cache local dos zips do release.
///
/// A carga mensal move 7,3 GB. Sem cache, corrigir um mapeamento e rodar de novo
/// custaria outro download completo - e o operador aprenderia a nao rodar de
/// novo, que e o pior resultado possivel para um pipeline de dados.
///
/// O digest e gravado num arquivo ao lado (<c>.sha256</c>) porque recalcular o
/// hash de 7,3 GB a cada execucao para descobrir que nada mudou e trabalho puro.
/// </summary>
public sealed class ReceitaFileCache(
    IReceitaFederalArchive archive,
    IOptions<ReceitaOptions> options,
    ILogger<ReceitaFileCache> logger)
{
    private readonly ReceitaOptions _options = options.Value;

    public string DirectoryFor(string release) =>
        Path.Combine(_options.CacheDirectory, release);

    public string PathFor(string release, string fileName) =>
        Path.Combine(DirectoryFor(release), fileName);

    /// <summary>
    /// Garante que o arquivo esta local e integro, e devolve seu digest.
    ///
    /// Integro aqui significa "tamanho igual ao declarado pela origem". Arquivo
    /// maior que o esperado e sinal de escrita corrompida - o download recomeca
    /// do zero, porque nao ha offset seguro do qual retomar.
    /// </summary>
    public async Task<ReceitaFileDigest> EnsureAsync(
        string release, ReceitaArchiveFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DirectoryFor(release));

        var path = PathFor(release, file.Name);
        var current = File.Exists(path) ? new FileInfo(path).Length : 0;

        if (current > file.Length)
        {
            logger.LogWarning(
                "{File} tem {Current} bytes contra {Expected} declarados na origem. Rebaixando do inicio.",
                file.Name, current, file.Length);

            File.Delete(path);
            SidecarDelete(path);
            current = 0;
        }

        if (current == file.Length && file.Length > 0)
        {
            return new ReceitaFileDigest(file.Name, file.Length, await DigestAsync(path, ct));
        }

        if (current > 0)
        {
            logger.LogInformation(
                "Retomando {File} em {Offset}/{Total} bytes.", file.Name, current, file.Length);
        }
        else
        {
            logger.LogInformation("Baixando {File} ({Total} bytes).", file.Name, file.Length);
        }

        await using (var remote = await archive.OpenAsync(release, file.Name, current, ct))
        await using (var local = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            local.Seek(current, SeekOrigin.Begin);
            await remote.CopyToAsync(local, ct);
        }

        var written = new FileInfo(path).Length;

        // O tamanho e a unica verificacao possivel contra a origem: a Receita nao
        // publica checksum. Falhar aqui e melhor que carregar um zip truncado,
        // que se manifestaria como "faltam 200 mil empresas" sem explicacao.
        if (written != file.Length)
        {
            throw new InvalidDataException(
                $"Download incompleto de '{file.Name}': {written} bytes, esperados {file.Length}.");
        }

        SidecarDelete(path);

        return new ReceitaFileDigest(file.Name, written, await DigestAsync(path, ct));
    }

    /// <summary>
    /// SHA-256 do arquivo, memorizado num sidecar. Nao serve para validar contra
    /// a origem - ela nao publica hash -, e sim para provar depois que a carga X
    /// leu exatamente estes bytes.
    /// </summary>
    private async Task<string> DigestAsync(string path, CancellationToken ct)
    {
        var sidecar = $"{path}.sha256";

        if (File.Exists(sidecar) && File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(path))
        {
            var cached = (await File.ReadAllTextAsync(sidecar, ct)).Trim();
            if (cached.Length == 64) return cached;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));

        await File.WriteAllTextAsync(sidecar, digest, ct);

        return digest;
    }

    private static void SidecarDelete(string path)
    {
        var sidecar = $"{path}.sha256";
        if (File.Exists(sidecar)) File.Delete(sidecar);
    }
}
