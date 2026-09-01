using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

/// <summary>
/// O slice do Website Auditor com portas falsas: sem Postgres, sem Hermes e sem
/// rede. E o que o <see cref="ExecuteWebsiteAuditUseCase"/> viver na Application,
/// e nao no Worker, torna possivel.
///
/// A ordem sonda-antes-do-agente e o que mais importa provar aqui: ela e uma
/// decisao de custo e de veracidade, e nao um detalhe de implementacao.
/// </summary>
public class ExecuteWebsiteAuditUseCaseTests
{
    // ------------------------------------------------------------- as falsas

    private sealed class FakeProbe(WebsiteProbeResult result) : IWebsiteProbe
    {
        public string Name => "fake-probe";
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        public Task<WebsiteProbeResult> ProbeAsync(string url, CancellationToken ct = default)
        {
            Calls++;
            LastUrl = url;
            return Task.FromResult(result with { RequestedUrl = url });
        }
    }

    private sealed class FakeAgentRuntime(params string[] responses) : IAgentRuntime
    {
        private int _index;
        public string Name => "fake";
        public List<AgentRunRequest> Requests { get; } = [];

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            var raw = _index < responses.Length ? responses[_index] : responses[^1];
            _index++;

            return Task.FromResult(new AgentRunResult
            {
                RawText = raw,
                Succeeded = true,
                ExternalRunId = $"fake-{_index}",
                InputTokens = 100,
                OutputTokens = 50,
                EstimatedCost = 0.01m
            });
        }
    }

    private sealed class FakeAuditPersister : IWebsiteAuditPersister
    {
        public WebsiteAuditPersistRequest? Persisted { get; private set; }

        public Task PersistAsync(WebsiteAuditPersistRequest request, CancellationToken ct = default)
        {
            Persisted = request;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditPrompts : IWebsiteAuditPromptBuilder
    {
        public string AgentName => "website-auditor";
        public string PromptVersion => "website-auditor-v1";
        public List<string> UserPrompts { get; } = [];

        public string BuildSystemPrompt() => "voce e o auditor";

        public string BuildUserPrompt(AccountContext context, WebsiteProbeResult probe)
        {
            var prompt = $"audite {probe.RequestedUrl}; ttfb={probe.TimeToFirstByte?.TotalMilliseconds}";
            UserPrompts.Add(prompt);
            return prompt;
        }

        public string BuildRepairPrompt(
            AccountContext context, WebsiteProbeResult probe, string previousOutput, string violations) =>
            $"corrija: {violations}";
    }

    /// <summary>
    /// Valida contra os records de contrato, sem JSON Schema: a Application nao
    /// conhece Json.Schema, e o que este teste exercita e o CICLO - validar,
    /// reparar, desistir -, nao a biblioteca de schema.
    /// </summary>
    private sealed class ContractValidator : IStructuredOutputValidator
    {
        public ValidationOutcome<T> Validate<T>(string rawText)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(rawText);

                return value is null
                    ? ValidationOutcome<T>.Fail(new SchemaViolation(string.Empty, "nulo"))
                    : ValidationOutcome<T>.Ok(value);
            }
            catch (JsonException ex)
            {
                return ValidationOutcome<T>.Fail(new SchemaViolation(string.Empty, ex.Message));
            }
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-08-31T12:00:00Z");
    }

    private sealed class SequentialIds : IIdentifierGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    // ------------------------------------------------------------- montagem

    private static WebsiteProbeResult Reachable() => new()
    {
        RequestedUrl = "https://grupoventosul.com.br",
        StatusCode = 200,
        TimeToFirstByte = TimeSpan.FromMilliseconds(180),
        DocumentBytes = 90_000,
        RenderBlockingResources = 1,
        CompressionEnabled = true,
        IsHttps = true,
        HasTitle = true,
        HasViewportMeta = true,
        HasFixedWidthViewport = false,
        Technologies =
        [
            new DetectedTechnology
            {
                Category = TechnologyCategory.Analytics, Name = "GA4", Match = "gtag/js?id=G-"
            }
        ],
        ObservedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z")
    };

    private static string ValidAudit() => JsonSerializer.Serialize(new
    {
        summary = "Site com vitrine propria e estoque tambem em portal.",
        audited_url = "https://grupoventosul.com.br",
        audit_completeness = 0.8m,
        evidence = new[]
        {
            new
            {
                claim_type = "inventory_count",
                claim_text = "Pagina 1 de 32.",
                confidence = 0.85m,
                source = new
                {
                    type = "website",
                    url = "https://grupoventosul.com.br/estoque",
                    observed_at = "2026-08-31T12:00:00Z"
                }
            }
        },
        inventory = new
        {
            published_online = true,
            approximate_count = 380,
            evidence_index = 0
        },
        portals = new[]
        {
            new { name = "Webmotors", evidence_index = 0 },
            new { name = "iCarros", evidence_index = 0 }
        }
    });

    private static (ExecuteWebsiteAuditUseCase UseCase, FakeProbe Probe,
                    FakeAuditPersister Persister, FakeAgentRuntime Runtime,
                    FakeAccountRepository Accounts, Guid AccountId, OutboxEvent Event)
        Build(WebsiteProbeResult probeResult, params string[] agentResponses)
    {
        var accounts = new FakeAccountRepository();
        var account = accounts.Add(AccountStatus.Researched, domain: "grupoventosul.com.br");

        var probe = new FakeProbe(probeResult);
        var persister = new FakeAuditPersister();
        var runtime = new FakeAgentRuntime(agentResponses.Length > 0 ? agentResponses : [ValidAudit()]);

        var runId = Guid.CreateVersion7();

        var evt = new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = EventTypes.AuditRequested,
            AggregateType = "account",
            AggregateId = account.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = account.Id,
                research_run_id = runId
            }),
            IdempotencyKey = IdempotencyKey.ForAudit(account.Id, runId),
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        };

        var useCase = new ExecuteWebsiteAuditUseCase(
            accounts,
            new FakeResearchRunRepository(),
            new FakeAgentRunRepository(),
            probe,
            persister,
            runtime,
            new ContractValidator(),
            new FakeAuditPrompts(),
            new FixedClock(),
            new SequentialIds(),
            NullLogger<ExecuteWebsiteAuditUseCase>.Instance);

        return (useCase, probe, persister, runtime, accounts, account.Id, evt);
    }

    // ---------------------------------------------------------------- testes

    [Fact]
    public async Task Mede_antes_de_chamar_o_agente_e_persiste_a_medicao_crua()
    {
        var (useCase, probe, persister, runtime, _, _, evt) = Build(Reachable());

        await useCase.ExecuteAsync(evt, TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Calls);
        Assert.Equal("https://grupoventosul.com.br", probe.LastUrl);

        Assert.NotNull(persister.Persisted);
        Assert.Equal(200, persister.Persisted!.Probe.StatusCode);
        Assert.NotNull(persister.Persisted.Profile);

        // O prompt do agente recebeu a medicao - e o que impede o modelo de
        // estimar o que a plataforma ja mediu.
        Assert.Contains("ttfb=180", runtime.Requests[0].UserPrompt);
    }

    /// <summary>
    /// Site fora do ar encerra o run SEM gastar modelo. Dominio morto e um
    /// resultado da auditoria, e forte; perguntar ao agente o que ele achou de
    /// uma pagina que nao existe seria pagar para receber alucinacao.
    /// </summary>
    [Fact]
    public async Task Site_inalcancavel_persiste_sem_chamar_o_agente()
    {
        var morto = WebsiteProbeResult.Unreachable(
            "https://grupoventosul.com.br", "DNS nao resolveu", DateTimeOffset.UtcNow);

        var (useCase, probe, persister, runtime, _, _, evt) = Build(morto);

        await useCase.ExecuteAsync(evt, TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Calls);
        Assert.Empty(runtime.Requests);

        Assert.NotNull(persister.Persisted);
        Assert.Null(persister.Persisted!.Profile);
        Assert.False(persister.Persisted.Score.Reachable);
    }

    /// <summary>
    /// A nota e da plataforma. O agente nao mandou numero nenhum, e mesmo assim a
    /// auditoria sai com as sete dimensoes calculadas sobre medicao e observacao.
    /// </summary>
    [Fact]
    public async Task A_nota_e_calculada_pela_plataforma_e_nao_vem_do_agente()
    {
        var (useCase, _, persister, _, _, _, evt) = Build(Reachable());

        await useCase.ExecuteAsync(evt, TestContext.Current.CancellationToken);

        var score = persister.Persisted!.Score;

        Assert.True(score.Reachable);
        Assert.NotNull(score.Performance);
        Assert.NotNull(score.Tracking);

        // Dois portais no payload do agente viram o fato de Technology Pain.
        Assert.True(score.MultiplePortals);
    }

    /// <summary>
    /// Uma tentativa de reparo, e uma so. O custo das duas chamadas e somado no
    /// agent_run: o orcamento gasto tem que aparecer inteiro, e nao so o da
    /// tentativa que deu certo.
    /// </summary>
    [Fact]
    public async Task Repara_uma_vez_e_soma_o_custo_das_duas_tentativas()
    {
        var (useCase, _, persister, runtime, _, _, evt) =
            Build(Reachable(), "isto nao e json", ValidAudit());

        await useCase.ExecuteAsync(evt, TestContext.Current.CancellationToken);

        Assert.Equal(2, runtime.Requests.Count);
        Assert.Contains("corrija:", runtime.Requests[1].UserPrompt);

        Assert.Equal(200, persister.Persisted!.AgentRun.InputTokens);
        Assert.Equal(0.02m, persister.Persisted.AgentRun.EstimatedCost);
    }

    [Fact]
    public async Task Segunda_falha_encerra_o_run_sem_escrita_parcial()
    {
        var (useCase, _, persister, runtime, _, _, evt) =
            Build(Reachable(), "nao e json", "continua nao sendo json");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(evt, TestContext.Current.CancellationToken));

        Assert.Equal(2, runtime.Requests.Count);
        Assert.Null(persister.Persisted);
    }

    /// <summary>
    /// Conta sem dominio falha ANTES da sonda e antes do agente. Quem descobre o
    /// dominio e o Researcher; mandar o auditor procurar faria ele achar qualquer
    /// site parecido e auditar a empresa errada.
    /// </summary>
    [Fact]
    public async Task Conta_sem_dominio_falha_sem_sondar_nem_chamar_agente()
    {
        var accounts = new FakeAccountRepository();
        var account = accounts.Add(AccountStatus.Researched, domain: null);

        var probe = new FakeProbe(Reachable());
        var runtime = new FakeAgentRuntime(ValidAudit());
        var persister = new FakeAuditPersister();

        var evt = new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = EventTypes.AuditRequested,
            AggregateType = "account",
            AggregateId = account.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = account.Id,
                research_run_id = Guid.CreateVersion7()
            }),
            IdempotencyKey = "audit:sem-dominio",
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        };

        var useCase = new ExecuteWebsiteAuditUseCase(
            accounts, new FakeResearchRunRepository(), new FakeAgentRunRepository(),
            probe, persister, runtime, new ContractValidator(), new FakeAuditPrompts(),
            new FixedClock(), new SequentialIds(),
            NullLogger<ExecuteWebsiteAuditUseCase>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(evt, TestContext.Current.CancellationToken));

        Assert.Equal(0, probe.Calls);
        Assert.Empty(runtime.Requests);
        Assert.Null(persister.Persisted);
    }

    /// <summary>
    /// A url do payload tem precedencia sobre accounts.domain: e o que permite
    /// auditar a vitrine em subdominio proprio, comum no setor.
    /// </summary>
    [Fact]
    public async Task Url_explicita_do_payload_vence_o_dominio_da_conta()
    {
        var accounts = new FakeAccountRepository();
        var account = accounts.Add(AccountStatus.Researched, domain: "grupoventosul.com.br");

        var probe = new FakeProbe(Reachable());

        var evt = new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = EventTypes.AuditRequested,
            AggregateType = "account",
            AggregateId = account.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = account.Id,
                research_run_id = Guid.CreateVersion7(),
                url = "https://seminovos.grupoventosul.com.br"
            }),
            IdempotencyKey = "audit:url-explicita",
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        };

        var useCase = new ExecuteWebsiteAuditUseCase(
            accounts, new FakeResearchRunRepository(), new FakeAgentRunRepository(),
            probe, new FakeAuditPersister(), new FakeAgentRuntime(ValidAudit()),
            new ContractValidator(), new FakeAuditPrompts(),
            new FixedClock(), new SequentialIds(),
            NullLogger<ExecuteWebsiteAuditUseCase>.Instance);

        await useCase.ExecuteAsync(evt, TestContext.Current.CancellationToken);

        Assert.Equal("https://seminovos.grupoventosul.com.br", probe.LastUrl);
    }
}
