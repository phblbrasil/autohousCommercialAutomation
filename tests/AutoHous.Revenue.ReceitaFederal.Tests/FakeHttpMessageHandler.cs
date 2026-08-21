using System.Net;
using System.Text;

namespace AutoHous.Revenue.ReceitaFederal.Tests;

/// <summary>
/// Handler roteado por caminho, e nao por fila.
///
/// O adaptador da Receita faz duas coisas em ordem imprevisivel para o teste -
/// descobre o token e so depois consulta o WebDAV -, entao uma fila de respostas
/// tornaria cada teste dependente da ordem interna do adaptador. Rotear por
/// caminho testa o que importa: o que ele pede e o que faz com a resposta.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Redirect da raiz para o compartilhamento: e dai que sai o token.</summary>
    public FakeHttpMessageHandler WithShare(string token)
    {
        _routes.Add((
            r => r.Method == HttpMethod.Get && !r.RequestUri!.AbsolutePath.Contains("/webdav/"),
            r =>
            {
                // O HttpClient real segue o 302 e deixa a URI final em
                // RequestMessage. Simular o estado final e o que o adaptador le.
                r.RequestUri = new Uri($"https://arquivos.receitafederal.gov.br/index.php/s/{token}");
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = r };
            }));

        return this;
    }

    public FakeHttpMessageHandler WithPropfind(string pathEndsWith, string xml)
    {
        _routes.Add((
            r => r.Method.Method == "PROPFIND" && r.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith(pathEndsWith),
            _ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            }));

        return this;
    }

    public FakeHttpMessageHandler WithFile(string fileName, byte[] content, bool honourRange = true)
    {
        _routes.Add((
            r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith(fileName),
            r =>
            {
                var from = (int?)r.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;

                if (from > 0 && !honourRange)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(content)
                    };
                }

                return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content[from..])
                };
            }));

        return this;
    }

    public FakeHttpMessageHandler WithStatus(string pathEndsWith, HttpStatusCode status)
    {
        _routes.Add((
            r => r.RequestUri!.AbsolutePath.EndsWith(pathEndsWith),
            _ => new HttpResponseMessage(status)));

        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        foreach (var (match, reply) in _routes)
        {
            if (match(request)) return Task.FromResult(reply(request));
        }

        throw new InvalidOperationException(
            $"Requisicao nao roteada: {request.Method} {request.RequestUri}");
    }
}
