using System.Net;
using System.Text;

namespace AutoHous.Revenue.Agents.Tests;

/// <summary>
/// Handler que devolve respostas gravadas em ordem e registra as requisicoes.
///
/// Permite exercitar o HermesAgentRuntime - o loop de status, o timeout, o retry
/// de 429 e a extracao do texto final - sem Hermes instalado e sem rede.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string json)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        return this;
    }

    /// <summary>Repete a mesma resposta indefinidamente (para loops de polling).</summary>
    public FakeHttpMessageHandler EnqueueRepeating(HttpStatusCode status, string json, int times)
    {
        for (var i = 0; i < times; i++) Enqueue(status, json);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("X-Hermes-Session-Id", out var s) ? s.FirstOrDefault() : null,
            request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"Requisicao inesperada #{Requests.Count}: {request.Method} {request.RequestUri}. " +
                "A fila de respostas acabou.");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}

public sealed record RecordedRequest(
    string Method, string Path, string? Authorization, string? SessionId, string? Body);
