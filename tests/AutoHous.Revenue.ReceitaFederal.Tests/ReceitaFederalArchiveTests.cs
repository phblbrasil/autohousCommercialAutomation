using System.Net;
using System.Text;
using AutoHous.Revenue.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.ReceitaFederal.Tests;

public class ReceitaFederalArchiveTests
{
    private const string Base = "https://arquivos.receitafederal.gov.br";

    private static string Multistatus(params (string Href, long? Length)[] entries)
    {
        var body = new StringBuilder("""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">""");

        foreach (var (href, length) in entries)
        {
            body.Append($"<d:response><d:href>{href}</d:href><d:propstat><d:prop>");
            body.Append("<d:getlastmodified>Sun, 09 Aug 2026 18:27:00 GMT</d:getlastmodified>");

            // Pasta nao declara getcontentlength - e o unico marcador confiavel
            // que o Nextcloud oferece nesta resposta.
            if (length is not null) body.Append($"<d:getcontentlength>{length}</d:getcontentlength>");

            body.Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
        }

        return body.Append("</d:multistatus>").ToString();
    }

    private static (ReceitaFederalArchive Archive, FakeHttpMessageHandler Handler) Build(
        FakeHttpMessageHandler handler, string? token = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(Base) };

        var options = Options.Create(new ReceitaOptions
        {
            BaseUrl = Base,
            ShareToken = token,
            BasePath = "Dados/Cadastros/CNPJ"
        });

        return (new ReceitaFederalArchive(http, options, NullLogger<ReceitaFederalArchive>.Instance), handler);
    }

    [Fact]
    public async Task Lista_competencias_e_ignora_o_que_nao_e_AAAA_MM()
    {
        var handler = new FakeHttpMessageHandler().WithPropfind("CNPJ", Multistatus(
            ("/public.php/webdav/Dados/Cadastros/CNPJ/", null),
            ("/public.php/webdav/Dados/Cadastros/CNPJ/2026-07/", null),
            ("/public.php/webdav/Dados/Cadastros/CNPJ/2026-08/", null),
            ("/public.php/webdav/Dados/Cadastros/CNPJ/2023-05/", null),
            // O tar.gz consolidado convive com as pastas mensais e nao e um release.
            ("/public.php/webdav/Dados/Cadastros/CNPJ/cnpj.tar.gz", 999L)));

        var (archive, _) = Build(handler, token: "TOKEN");

        var releases = await archive.ListReleasesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["2023-05", "2026-07", "2026-08"], releases);
    }

    [Fact]
    public async Task A_propria_pasta_consultada_nao_vira_filha_de_si_mesma()
    {
        var handler = new FakeHttpMessageHandler().WithPropfind("2026-08", Multistatus(
            ("/public.php/webdav/Dados/Cadastros/CNPJ/2026-08/", null),
            ("/public.php/webdav/Dados/Cadastros/CNPJ/2026-08/Cnaes.zip", 22078L)));

        var (archive, _) = Build(handler, token: "TOKEN");

        var files = await archive.ListFilesAsync("2026-08", TestContext.Current.CancellationToken);

        Assert.Equal("Cnaes.zip", Assert.Single(files).Name);
    }

    [Fact]
    public async Task Tamanho_e_data_vem_do_propfind()
    {
        var handler = new FakeHttpMessageHandler().WithPropfind("2026-08", Multistatus(
            ("/public.php/webdav/Dados/Cadastros/CNPJ/2026-08/Estabelecimentos0.zip", 2_200_000_000L)));

        var (archive, _) = Build(handler, token: "TOKEN");

        var file = Assert.Single(await archive.ListFilesAsync("2026-08", TestContext.Current.CancellationToken));

        // Sem o tamanho declarado nao ha como saber se o download terminou: a
        // Receita nao publica checksum.
        Assert.Equal(2_200_000_000L, file.Length);
        Assert.Equal(2026, file.LastModified.Year);
    }

    [Fact]
    public async Task Nome_com_escape_de_url_e_decodificado()
    {
        var handler = new FakeHttpMessageHandler().WithPropfind("2026-08", Multistatus(
            ("/public.php/webdav/Dados/Cadastros/CNPJ/2026-08/Qualifica%C3%A7oes.zip", 100L)));

        var (archive, _) = Build(handler, token: "TOKEN");

        Assert.Equal(
            "Qualificaçoes.zip",
            Assert.Single(await archive.ListFilesAsync("2026-08", TestContext.Current.CancellationToken)).Name);
    }

    [Fact]
    public async Task Token_e_descoberto_do_redirect_quando_nao_configurado()
    {
        // O repositorio ja migrou de plataforma uma vez e os caminhos antigos
        // hoje dao 404. Token fixado no codigo garante que a proxima migracao
        // derrube a carga mensal sem aviso.
        var handler = new FakeHttpMessageHandler()
            .WithShare("DESCOBERTO")
            .WithPropfind("CNPJ", Multistatus(("/public.php/webdav/Dados/Cadastros/CNPJ/2026-08/", null)));

        var (archive, _) = Build(handler, token: null);

        await archive.ListReleasesAsync(TestContext.Current.CancellationToken);

        var propfind = handler.Requests.Single(r => r.Method.Method == "PROPFIND");
        var credentials = Encoding.ASCII.GetString(
            Convert.FromBase64String(propfind.Headers.Authorization!.Parameter!));

        // Compartilhamento publico do Nextcloud: token como usuario, senha vazia.
        Assert.Equal("DESCOBERTO:", credentials);
    }

    [Fact]
    public async Task Token_configurado_dispensa_a_descoberta()
    {
        var handler = new FakeHttpMessageHandler()
            .WithPropfind("CNPJ", Multistatus(("/public.php/webdav/Dados/Cadastros/CNPJ/2026-08/", null)));

        var (archive, _) = Build(handler, token: "DO-AMBIENTE");

        await archive.ListReleasesAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Caminho_inexistente_explica_como_conferir()
    {
        var handler = new FakeHttpMessageHandler().WithStatus("1999-01", HttpStatusCode.NotFound);
        var (archive, _) = Build(handler, token: "TOKEN");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => archive.ListFilesAsync("1999-01", TestContext.Current.CancellationToken));

        Assert.Contains("--list", error.Message);
    }

    [Fact]
    public async Task Open_com_offset_pede_range()
    {
        var handler = new FakeHttpMessageHandler().WithFile("Cnaes.zip", [1, 2, 3, 4, 5, 6, 7, 8]);
        var (archive, _) = Build(handler, token: "TOKEN");

        await using var stream = await archive.OpenAsync(
            "2026-08", "Cnaes.zip", 4, TestContext.Current.CancellationToken);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal<byte[]>([5, 6, 7, 8], buffer.ToArray());
        Assert.Equal(4, handler.Requests[^1].Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task Origem_que_ignora_range_falha_em_vez_de_corromper_o_arquivo()
    {
        // Um 200 no lugar de 206 significa "aqui esta o arquivo inteiro". Escrever
        // isso a partir do offset produziria um zip corrompido de tamanho
        // plausivel - o pior desfecho possivel, porque parece sucesso.
        var handler = new FakeHttpMessageHandler()
            .WithFile("Empresas0.zip", [1, 2, 3, 4], honourRange: false);

        var (archive, _) = Build(handler, token: "TOKEN");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => archive.OpenAsync("2026-08", "Empresas0.zip", 2, TestContext.Current.CancellationToken));

        Assert.Contains("Range", error.Message);
    }
}
