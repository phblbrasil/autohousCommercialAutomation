using AutoHous.Revenue.Application;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Cliente do Hermes API Server.
///
/// Autenticacao Bearer com API_SERVER_KEY em toda requisicao. O
/// X-Hermes-Session-Id recebe o research_run_id, de modo que um run seja
/// rastreavel dos dois lados.
///
/// AVISO: a documentacao publica do Hermes lista os endpoints de /v1/runs mas
/// nao fixa o envelope exato das respostas. A extracao abaixo e deliberadamente
/// tolerante e deve ser confirmada contra o servidor real na ativacao (E11);
/// se divergir, HermesOptions.Transport = Chat usa o envelope OpenAI, que e
/// especificado com exatidao.
/// </summary>
public sealed class HermesAgentRuntime : IAgentRuntime
{
    private readonly HttpClient _http;
    private readonly HermesOptions _options;
    private readonly ILogger<HermesAgentRuntime>? _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public string Name => "hermes";

    public HermesAgentRuntime(
        HttpClient http,
        IOptions<HermesOptions> options,
        ILogger<HermesAgentRuntime>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        _pipeline = BuildPipeline(_options, logger);
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RunTimeout);

        try
        {
            return _options.Transport == HermesTransport.Chat
                ? await RunViaChatAsync(request, stopwatch, timeout.Token)
                : await RunViaRunsAsync(request, stopwatch, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AgentRunResult.Failure(
                $"Run excedeu o timeout de {_options.RunTimeout.TotalMinutes:0.#} min.");
        }
        catch (HttpRequestException ex)
        {
            return AgentRunResult.Failure($"Falha de transporte com o Hermes: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- /v1/runs

    private async Task<AgentRunResult> RunViaRunsAsync(
        AgentRunRequest request, Stopwatch stopwatch, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["instructions"] = request.SystemPrompt,
            ["input"] = request.UserPrompt,
            ["metadata"] = ToMetadata(request)
        };

        // O X-Hermes-Session-Id existe, mas so /v1/chat/completions o le - em
        // /v1/runs a sessao vem do corpo. Sem este campo o run abre sessao nova
        // a cada tentativa e o research_run_id deixa de casar dos dois lados.
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            payload["session_id"] = request.SessionId;
        }

        using var created = await SendAsync(HttpMethod.Post, "/v1/runs", payload, request.SessionId, ct);
        var createdBody = await ReadJsonAsync(created, ct);

        if (!created.IsSuccessStatusCode)
        {
            return AgentRunResult.Failure(
                $"POST /v1/runs retornou {(int)created.StatusCode}: {Truncate(createdBody?.ToJsonString())}");
        }

        var runId = FindString(createdBody, "run_id", "id");

        if (string.IsNullOrWhiteSpace(runId))
        {
            return AgentRunResult.Failure(
                $"POST /v1/runs nao retornou run_id. Corpo: {Truncate(createdBody?.ToJsonString())}");
        }

        _logger?.LogInformation("Hermes run {RunId} criado para o agente {Agent}", runId, request.AgentName);

        // Polling em vez de SSE: para um run unico de pesquisa o ganho de latencia
        // do stream nao compensa a fragilidade de manter a conexao aberta por
        // minutos. Os eventos permanecem disponiveis em /v1/runs/{id}/events para
        // depuracao e para uma futura UI de acompanhamento.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(_options.PollInterval, ct);

            using var polled = await SendAsync(HttpMethod.Get, $"/v1/runs/{runId}", null, request.SessionId, ct);
            var body = await ReadJsonAsync(polled, ct);
            var status = FindString(body, "status")?.ToLowerInvariant();

            if (status is "completed" or "succeeded" or "done")
            {
                stopwatch.Stop();
                return Build(body, runId, stopwatch.Elapsed);
            }

            if (status is "failed" or "error" or "cancelled" or "canceled")
            {
                return AgentRunResult.Failure(
                    $"Run {runId} terminou com status '{status}': {Truncate(FindString(body, "error"))}",
                    runId);
            }
        }
    }

    // --------------------------------------------------- /v1/chat/completions

    private async Task<AgentRunResult> RunViaChatAsync(
        AgentRunRequest request, Stopwatch stopwatch, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt })
        };

        using var response = await SendAsync(
            HttpMethod.Post, "/v1/chat/completions", payload, request.SessionId, ct);

        var body = await ReadJsonAsync(response, ct);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            return AgentRunResult.Failure(
                $"POST /v1/chat/completions retornou {(int)response.StatusCode}: {Truncate(body?.ToJsonString())}");
        }

        var content = FirstChoiceContent(body);

        return string.IsNullOrWhiteSpace(content)
            ? AgentRunResult.Failure("Resposta de chat/completions sem conteudo de assistente.")
            : Build(body, FindString(body, "id"), stopwatch.Elapsed, content);
    }

    // ------------------------------------------------------------- utilitarios

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, JsonNode? body, string? sessionId, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async token =>
        {
            using var message = new HttpRequestMessage(method, path);

            if (body is not null)
            {
                message.Content = JsonContent.Create(body);
            }

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                message.Headers.TryAddWithoutValidation("X-Hermes-Session-Id", sessionId);
            }

            return await _http.SendAsync(message, token);
        }, ct);

    private static JsonObject ToMetadata(AgentRunRequest request)
    {
        var metadata = new JsonObject
        {
            ["agent"] = request.AgentName,
            ["prompt_version"] = request.PromptVersion
        };

        foreach (var (key, value) in request.Metadata)
        {
            metadata[key] = value;
        }

        return metadata;
    }

    private static AgentRunResult Build(
        JsonNode? body, string? runId, TimeSpan duration, string? explicitText = null)
    {
        var usage = body?["usage"];

        return new AgentRunResult
        {
            ExternalRunId = runId,
            RawText = explicitText ?? ExtractText(body) ?? string.Empty,
            Succeeded = true,
            ModelProvider = FindString(body, "provider"),
            ModelName = FindString(body, "model"),
            InputTokens = AsInt(usage?["prompt_tokens"] ?? usage?["input_tokens"]),
            OutputTokens = AsInt(usage?["completion_tokens"] ?? usage?["output_tokens"]),
            EstimatedCost = AsDecimal(usage?["cost"] ?? body?["cost"]),
            Duration = duration
        };
    }

    /// <summary>
    /// Procura o texto final do assistente nas formas conhecidas de resposta.
    /// Tolerante de proposito: ver o aviso no cabecalho da classe.
    /// </summary>
    private static string? ExtractText(JsonNode? body)
    {
        if (body is null) return null;

        // A forma que o servidor real usa: o envelope "hermes.run" devolve o
        // texto final em output, como STRING - nao como array da Responses API.
        // Verificado em gateway/platforms/api_server.py (_set_run_status com
        // output=final_response). Vem primeiro porque e o unico caminho que
        // executa em producao; os demais continuam por tolerancia.
        if (body["output"] is JsonValue outputValue
            && outputValue.TryGetValue<string>(out var outputText)
            && !string.IsNullOrWhiteSpace(outputText))
        {
            return outputText;
        }

        var direct = body["output_text"]?.GetValue<string>()
                     ?? body["result"]?["output_text"]?.GetValue<string>()
                     ?? FirstChoiceContent(body);

        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        // Formato Responses API: output[] com content[] de type "output_text".
        if (body["output"] is JsonArray output)
        {
            foreach (var item in output.Reverse())
            {
                if (item?["content"] is not JsonArray content) continue;

                foreach (var part in content)
                {
                    var text = part?["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }

        return body["result"]?.GetValue<string>();
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);

        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindString(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = node?[key];

            if (value is null) continue;
            if (value is JsonValue v && v.TryGetValue<string>(out var s)) return s;

            return value.ToJsonString();
        }

        return null;
    }

    /// <summary>
    /// Acesso seguro a choices[0].message.content.
    ///
    /// O indexador de JsonNode LANCA em array vazio em vez de devolver null, entao
    /// um "choices": [] do servidor viraria excecao nao tratada no worker.
    /// </summary>
    private static string? FirstChoiceContent(JsonNode? body)
    {
        if (body?["choices"] is not JsonArray { Count: > 0 } choices) return null;

        return choices[0]?["message"]?["content"] is JsonValue value
               && value.TryGetValue<string>(out var content)
            ? content
            : null;
    }

    private static int? AsInt(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static decimal? AsDecimal(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    private static string Truncate(string? text, int max = 400) =>
        string.IsNullOrEmpty(text) ? string.Empty
            : text.Length <= max ? text : text[..max] + "...";

    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(
        HermesOptions options, ILogger? logger) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,
                // 429 e o cap de runs concorrentes do Hermes (padrao 10): um lote
                // de pesquisas derrubaria a propria fila sem este retry.
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r =>
                        r.StatusCode == HttpStatusCode.TooManyRequests ||
                        (int)r.StatusCode >= 500)
                    .Handle<HttpRequestException>(),
                OnRetry = args =>
                {
                    logger?.LogWarning(
                        "Retry {Attempt} para o Hermes apos {Status}",
                        args.AttemptNumber,
                        args.Outcome.Result?.StatusCode.ToString() ?? args.Outcome.Exception?.GetType().Name);
                    return default;
                }
            })
            .Build();
}
