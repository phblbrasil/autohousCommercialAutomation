using System.Security.Cryptography;
using AutoHous.Revenue.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.ReceitaFederal.Tests;

public class ReceitaFileCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("receita-cache").FullName;
    private readonly byte[] _content = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class CountingArchive(byte[] content) : IReceitaFederalArchive
    {
        public List<long> Offsets { get; } = [];

        public Task<IReadOnlyList<string>> ListReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["2026-08"]);

        public Task<IReadOnlyList<ReceitaArchiveFile>> ListFilesAsync(string release, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReceitaArchiveFile>>(
                [new ReceitaArchiveFile("Cnaes.zip", content.Length, DateTimeOffset.UnixEpoch)]);

        public Task<Stream> OpenAsync(string release, string fileName, long offset, CancellationToken ct = default)
        {
            Offsets.Add(offset);
            return Task.FromResult<Stream>(new MemoryStream(content[(int)offset..]));
        }
    }

    private (ReceitaFileCache Cache, CountingArchive Archive) Build()
    {
        var archive = new CountingArchive(_content);

        var cache = new ReceitaFileCache(
            archive,
            Options.Create(new ReceitaOptions { CacheDirectory = _dir }),
            NullLogger<ReceitaFileCache>.Instance);

        return (cache, archive);
    }

    private ReceitaArchiveFile File_ => new("Cnaes.zip", _content.Length, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Baixa_e_devolve_o_digest()
    {
        var (cache, _) = Build();

        var digest = await cache.EnsureAsync("2026-08", File_, TestContext.Current.CancellationToken);

        Assert.Equal(_content.Length, digest.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(_content)), digest.Sha256);
    }

    [Fact]
    public async Task Arquivo_integro_nao_e_rebaixado()
    {
        // A carga mensal move 7,3 GB. Sem cache, corrigir um mapeamento e rodar
        // de novo custaria outro download completo - e o operador aprenderia a
        // nao rodar de novo.
        var (cache, archive) = Build();

        await cache.EnsureAsync("2026-08", File_, TestContext.Current.CancellationToken);
        await cache.EnsureAsync("2026-08", File_, TestContext.Current.CancellationToken);

        Assert.Single(archive.Offsets);
    }

    [Fact]
    public async Task Arquivo_parcial_e_retomado_do_ponto_em_que_parou()
    {
        var (cache, archive) = Build();

        var path = cache.PathFor("2026-08", "Cnaes.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, _content[..100], TestContext.Current.CancellationToken);

        var digest = await cache.EnsureAsync("2026-08", File_, TestContext.Current.CancellationToken);

        Assert.Equal(100, Assert.Single(archive.Offsets));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(_content)), digest.Sha256);
        Assert.Equal(_content, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Arquivo_maior_que_o_declarado_recomeca_do_zero()
    {
        // Nao ha offset seguro do qual retomar um arquivo que ja passou do
        // tamanho: o excesso e escrita corrompida.
        var (cache, archive) = Build();

        var path = cache.PathFor("2026-08", "Cnaes.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[_content.Length + 50], TestContext.Current.CancellationToken);

        await cache.EnsureAsync("2026-08", File_, TestContext.Current.CancellationToken);

        Assert.Equal(0, Assert.Single(archive.Offsets));
    }

    [Fact]
    public async Task Download_que_nao_completa_falha_em_vez_de_seguir()
    {
        // Um zip truncado se manifestaria como "faltam 200 mil empresas" sem
        // explicacao, muito depois, quando o operador ja considerou a carga boa.
        var archive = new CountingArchive(_content[..10]);

        var cache = new ReceitaFileCache(
            archive,
            Options.Create(new ReceitaOptions { CacheDirectory = _dir }),
            NullLogger<ReceitaFileCache>.Instance);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => cache.EnsureAsync("2026-08", File_, TestContext.Current.CancellationToken));

        Assert.Contains("Download incompleto", error.Message);
    }

    [Fact]
    public async Task Digest_e_memorizado_num_sidecar()
    {
        // Recalcular o hash de 7,3 GB a cada execucao para descobrir que nada
        // mudou e trabalho puro.
        var (cache, _) = Build();

        await cache.EnsureAsync("2026-08", File_, TestContext.Current.CancellationToken);

        var sidecar = $"{cache.PathFor("2026-08", "Cnaes.zip")}.sha256";

        Assert.True(File.Exists(sidecar));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(_content)),
            (await File.ReadAllTextAsync(sidecar, TestContext.Current.CancellationToken)).Trim());
    }
}
