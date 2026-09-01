using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Application;
using System.Net;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.Agents.Tests;

/// <summary>
/// Cobre o caminho que so vai executar de verdade na ativacao (ARI-58): criacao
/// do run, loop de status, extracao do texto final, retry de 429 e timeout.
///
/// A doc publica do Hermes lista os endpoints de /v1/runs mas nao fixa o envelope
/// das respostas. Estes testes documentam as formas que o cliente aceita - se o
/// servidor real divergir, e aqui que a divergencia fica visivel.
///
/// O envelope foi conferido duas vezes, e a segunda contradisse a primeira:
///
/// - 20/08/2026: <c>_set_run_status</c> recebia <c>output</c> como string.
///   Fixado em <see cref="Extrai_texto_do_envelope_real_do_hermes_run"/>.
/// - 31/08/2026, v0.21.0: <c>output</c> nao e mais passado em call site nenhum -
///   <c>GET /v1/runs/{id}</c> nao carrega o texto final. Fixado em
///   <see cref="Run_que_completa_sem_texto_falha_dizendo_o_remedio"/>, e a razao
///   de o padrao ter virado <see cref="HermesTransport.Chat"/>.
///
/// Os dois continuam cobertos: o primeiro porque o cliente precisa aceita-lo se
/// o gateway voltar atras, o segundo porque e o que acontece hoje. Os demais
/// formatos seguem cobertos por tolerancia, nao por observacao.
/// </summary>
public class HermesAgentRuntimeTests
{
    private const string RunCreated = """{"run_id":"run_abc123","status":"queued"}""";

    private static (HermesAgentRuntime Runtime, FakeHttpMessageHandler Handler) Build(
        Action<FakeHttpMessageHandler> arrange, Action<HermesOptions>? configure = null)
    {
        var handler = new FakeHttpMessageHandler();
        arrange(handler);

        var options = new HermesOptions
        {
            BaseUrl = "http://127.0.0.1:8642",
            ApiKey = "chave-de-teste",
            PollInterval = TimeSpan.FromMilliseconds(1),
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            RunTimeout = TimeSpan.FromSeconds(5),

            // Fixado, e nao herdado do padrao. A maioria dos testes deste arquivo
            // exercita /v1/runs, e ate aqui eles dependiam de Runs ser o default -
            // de modo que a troca do padrao para Chat, feita porque o v0.21.0
            // nao devolve o texto no status do run, derrubou dezoito testes que
            // nao tinham nada a ver com a mudanca. Um teste de transporte declara
            // o transporte que testa; os de Chat ja faziam isso.
            Transport = HermesTransport.Runs
        };

        configure?.Invoke(options);

        var http = new HttpClient(handler);
        return (new HermesAgentRuntime(http, Options.Create(options)), handler);
    }

    private static AgentRunRequest Request() => new()
    {
        AgentName = "researcher",
        PromptVersion = "researcher-v1",
        SystemPrompt = "voce e o pesquisador",
        UserPrompt = "pesquise a conta",
        SessionId = "0199aa11-2233-4455-6677-889900aabbcc"
    };

    // ------------------------------------------------------------- /v1/runs

    [Fact]
    public async Task Cria_run_e_extrai_o_texto_final_apos_o_polling()
    {
        var (runtime, handler) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """{"status":"running"}""")
            .Enqueue(HttpStatusCode.OK, """
                {"status":"completed","output_text":"{\"segment\":\"dealer_group\"}",
                 "model":"hermes-agent","usage":{"input_tokens":1200,"output_tokens":800,"cost":0.042}}
                """));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("run_abc123", result.ExternalRunId);
        Assert.Contains("dealer_group", result.RawText);
        Assert.Equal(1200, result.InputTokens);
        Assert.Equal(800, result.OutputTokens);
        Assert.Equal(0.042m, result.EstimatedCost);

        // POST para criar, depois GETs de status.
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("/v1/runs", handler.Requests[0].Path);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Equal("/v1/runs/run_abc123", handler.Requests[1].Path);
    }

    [Fact]
    public async Task Envia_bearer_e_session_id_em_toda_requisicao()
    {
        var (runtime, handler) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """{"status":"completed","output_text":"ok"}"""));

        await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer chave-de-teste", r.Authorization);
            Assert.Equal("0199aa11-2233-4455-6677-889900aabbcc", r.SessionId);
        });
    }

    [Fact]
    public async Task Envia_agente_e_versao_do_prompt_como_metadata()
    {
        var (runtime, handler) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """{"status":"completed","output_text":"ok"}"""));

        await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        var body = handler.Requests[0].Body!;
        Assert.Contains("\"agent\":\"researcher\"", body);
        Assert.Contains("\"prompt_version\":\"researcher-v1\"", body);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("cancelled")]
    [InlineData("canceled")]
    public async Task Run_com_status_terminal_de_erro_falha(string status)
    {
        var (runtime, _) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, $$"""{"status":"{{status}}","error":"o modelo recusou"}"""));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(status, result.Error);
        Assert.Equal("run_abc123", result.ExternalRunId);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("succeeded")]
    [InlineData("done")]
    public async Task Aceita_as_variantes_de_status_de_sucesso(string status)
    {
        var (runtime, _) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, $$"""{"status":"{{status}}","output_text":"pronto"}"""));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("pronto", result.RawText);
    }

    [Fact]
    public async Task Falha_quando_a_criacao_do_run_nao_devolve_run_id()
    {
        var (runtime, _) = Build(h => h.Enqueue(HttpStatusCode.Accepted, """{"status":"queued"}"""));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("run_id", result.Error);
    }

    [Fact]
    public async Task Falha_legivel_quando_a_criacao_do_run_e_recusada()
    {
        var (runtime, _) = Build(h => h
            .EnqueueRepeating(HttpStatusCode.BadRequest, """{"error":"modelo desconhecido"}""", 1));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("400", result.Error);
        Assert.Contains("modelo desconhecido", result.Error);
    }

    // ----------------------------------------------------------- resiliencia

    [Fact]
    public async Task Faz_retry_no_429_do_cap_de_runs_concorrentes()
    {
        // O Hermes limita runs concorrentes (padrao 10) e devolve 429 ao estourar.
        // Sem retry, um lote de pesquisas derrubaria a propria fila.
        var (runtime, handler) = Build(h => h
            .Enqueue(HttpStatusCode.TooManyRequests, """{"error":"too many concurrent runs"}""")
            .Enqueue(HttpStatusCode.TooManyRequests, """{"error":"too many concurrent runs"}""")
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """{"status":"completed","output_text":"ok"}"""));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(4, handler.Requests.Count);   // 2 recusas + criacao + status
    }

    [Fact]
    public async Task Faz_retry_em_erro_5xx()
    {
        var (runtime, handler) = Build(h => h
            .Enqueue(HttpStatusCode.ServiceUnavailable, """{"error":"reiniciando"}""")
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """{"status":"completed","output_text":"ok"}"""));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Nao_faz_retry_em_erro_de_contrato_4xx()
    {
        // 400 e erro nosso: repetir so gasta tempo e cota.
        var (runtime, handler) = Build(h => h.Enqueue(HttpStatusCode.BadRequest, """{"error":"payload invalido"}"""));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Desiste_apos_o_timeout_do_run()
    {
        // Run que nunca sai de 'running'.
        var (runtime, _) = Build(
            h => h.Enqueue(HttpStatusCode.Accepted, RunCreated)
                  .EnqueueRepeating(HttpStatusCode.OK, """{"status":"running"}""", 500),
            o => o.RunTimeout = TimeSpan.FromMilliseconds(150));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("timeout", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelamento_externo_propaga()
    {
        using var cts = new CancellationTokenSource();

        var (runtime, _) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .EnqueueRepeating(HttpStatusCode.OK, """{"status":"running"}""", 500));

        cts.CancelAfter(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RunAsync(Request(), cts.Token));
    }

    // ------------------------------------------ envelopes alternativos de saida

    [Fact]
    public async Task Extrai_texto_do_envelope_real_do_hermes_run()
    {
        // Envelope conferido em 20/08/2026, quando _set_run_status ainda recebia
        // output como STRING. Continua coberto porque o cliente precisa aceita-lo
        // se o gateway voltar a expo-lo - mas NAO e mais o comportamento do
        // servidor: ver Run_que_completa_sem_texto_falha_dizendo_o_remedio, que
        // fixa o v0.21.0.
        var (runtime, _) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, """{"run_id":"run_abc123","status":"started"}""")
            .Enqueue(HttpStatusCode.OK, """{"object":"hermes.run","run_id":"run_abc123","status":"running"}""")
            .Enqueue(HttpStatusCode.OK, """
                {"object":"hermes.run","run_id":"run_abc123","status":"completed",
                 "session_id":"0199aa11-2233-4455-6677-889900aabbcc","model":"hermes-agent",
                 "output":"{\"segment\":\"dealer_group\"}","last_event":"run.completed",
                 "usage":{"input_tokens":1200,"output_tokens":800,"total_tokens":2000}}
                """));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("""{"segment":"dealer_group"}""", result.RawText);
        Assert.Equal("run_abc123", result.ExternalRunId);
        Assert.Equal(1200, result.InputTokens);
        Assert.Equal(800, result.OutputTokens);
    }

    /// <summary>
    /// O envelope REAL do Hermes v0.21.0, conferido em 31/08/2026 contra a
    /// instalacao local: <c>_handle_get_run</c> devolve o dicionario de
    /// <c>_run_statuses</c>, e nenhuma chamada a <c>_set_run_status</c> passa
    /// <c>output</c>. O texto so passa pelo evento assistant.completed da SSE,
    /// numa fila sem historico.
    ///
    /// O que este teste protege nao e a extracao - e o MODO DE FALHAR. Sem a
    /// checagem, o run voltaria Succeeded com RawText vazio, o validador
    /// reprovaria como contract_violation, e quem lesse o erro concluiria que o
    /// modelo nao sabe formatar JSON. A investigacao comecaria pelo prompt e
    /// nunca chegaria ao cliente HTTP.
    /// </summary>
    [Fact]
    public async Task Run_que_completa_sem_texto_falha_dizendo_o_remedio()
    {
        var (runtime, _) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, """{"run_id":"run_abc123","status":"started"}""")
            .Enqueue(HttpStatusCode.OK, """
                {"object":"hermes.run","run_id":"run_abc123","status":"completed",
                 "session_id":"0199aa11-2233-4455-6677-889900aabbcc",
                 "last_event":"run.completed",
                 "usage":{"input_tokens":1200,"output_tokens":800,"total_tokens":2000}}
                """));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("run_abc123", result.ExternalRunId);
        Assert.Contains("sem texto", result.Error);
        Assert.Contains("Transport = Chat", result.Error);
    }

    /// <summary>
    /// O padrao tem que ser Chat: e o unico transporte que funciona contra o
    /// gateway instalado. Um teste sobre o valor de um default parece excessivo
    /// ate lembrar que o custo de errar aqui e 100% dos runs reais falhando com
    /// uma mensagem que aponta para o lugar errado.
    /// </summary>
    [Fact]
    public void O_transporte_padrao_e_chat()
    {
        Assert.Equal(HermesTransport.Chat, new HermesOptions().Transport);
    }

    [Fact]
    public async Task Manda_o_prompt_de_sistema_em_instructions_e_a_sessao_no_corpo()
    {
        // instructions e anexado ao system prompt do proprio Hermes
        // (conversation_loop.py junta os dois), entao o researcher chega como
        // instrucao de sistema em vez de virar preambulo do turno do usuario.
        // session_id vai no corpo porque /v1/runs ignora o cabecalho de sessao.
        var (runtime, handler) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """{"status":"completed","output":"ok"}"""));

        await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        var body = handler.Requests[0].Body!;
        Assert.Contains("\"instructions\":\"voce e o pesquisador\"", body);
        Assert.Contains("\"input\":\"pesquise a conta\"", body);
        Assert.Contains("\"session_id\":\"0199aa11-2233-4455-6677-889900aabbcc\"", body);
    }

    [Fact]
    public async Task Extrai_texto_do_formato_responses_api()
    {
        // output[].content[].text - formato da Responses API.
        var (runtime, _) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """
                {"status":"completed",
                 "output":[{"content":[{"type":"output_text","text":"perfil em json"}]}]}
                """));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("perfil em json", result.RawText);
    }

    [Fact]
    public async Task Extrai_texto_do_formato_de_chat_dentro_do_run()
    {
        var (runtime, _) = Build(h => h
            .Enqueue(HttpStatusCode.Accepted, RunCreated)
            .Enqueue(HttpStatusCode.OK, """
                {"status":"completed","choices":[{"message":{"role":"assistant","content":"resposta"}}]}
                """));

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("resposta", result.RawText);
    }

    // ------------------------------------------------- transporte alternativo

    [Fact]
    public async Task Transporte_chat_usa_o_envelope_openai_em_uma_unica_chamada()
    {
        // Caminho de contingencia: /v1/chat/completions tem envelope especificado
        // com exatidao, ao contrario de /v1/runs.
        var (runtime, handler) = Build(
            h => h.Enqueue(HttpStatusCode.OK, """
                {"id":"chatcmpl-1","model":"hermes-agent",
                 "choices":[{"message":{"role":"assistant","content":"{\"segment\":\"dealership\"}"}}],
                 "usage":{"prompt_tokens":900,"completion_tokens":300}}
                """),
            o => o.Transport = HermesTransport.Chat);

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("dealership", result.RawText);
        Assert.Equal(900, result.InputTokens);
        Assert.Equal(300, result.OutputTokens);

        Assert.Single(handler.Requests);
        Assert.Equal("/v1/chat/completions", handler.Requests[0].Path);
        Assert.Contains("\"stream\":false", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Transporte_chat_falha_quando_nao_ha_conteudo_de_assistente()
    {
        var (runtime, _) = Build(
            h => h.Enqueue(HttpStatusCode.OK, """{"id":"chatcmpl-1","choices":[]}"""),
            o => o.Transport = HermesTransport.Chat);

        var result = await runtime.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("sem conteudo", result.Error);
    }

    [Fact]
    public void Identifica_se_como_hermes()
    {
        var (runtime, _) = Build(_ => { });
        Assert.Equal("hermes", runtime.Name);
    }
}
