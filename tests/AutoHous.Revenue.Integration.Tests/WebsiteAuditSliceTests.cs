using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Worker;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// A fatia do Website Auditor ponta a ponta: evento no outbox -> worker -> sonda
/// -> agente (fixture) -> validacao -> persistencia transacional -> scoring.
///
/// A sonda e a UNICA peca substituida, porque a de producao faz HTTP real. Todo o
/// resto - dispatcher, validador com dois schemas, guard, persister, Postgres de
/// verdade com as migrations aplicadas - e o caminho de producao.
/// </summary>
public class WebsiteAuditSliceTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private ServiceProvider _services = null!;
    private StubProbe _probe = null!;

    /// <summary>
    /// Sonda determinista. Devolve o que o teste mandar, sem tocar na rede.
    /// </summary>
    private sealed class StubProbe : IWebsiteProbe
    {
        public string Name => "stub";
        public WebsiteProbeResult Next { get; set; } = Reachable();
        public List<string> Probed { get; } = [];

        public Task<WebsiteProbeResult> ProbeAsync(string url, CancellationToken ct = default)
        {
            Probed.Add(url);
            return Task.FromResult(Next with { RequestedUrl = url });
        }

        public static WebsiteProbeResult Reachable() => new()
        {
            RequestedUrl = "https://grupoventosul.com.br",
            FinalUrl = "https://grupoventosul.com.br/",
            StatusCode = 200,
            TimeToFirstByte = TimeSpan.FromMilliseconds(210),
            DocumentLoadTime = TimeSpan.FromMilliseconds(640),
            DocumentBytes = 120_000,
            RenderBlockingResources = 2,
            CompressionEnabled = true,
            IsHttps = true,
            HasTitle = true,
            HasMetaDescription = true,
            HasH1 = true,
            HasCanonical = true,
            HasStructuredData = false,
            HasSitemap = true,
            HasRobotsTxt = true,
            HasViewportMeta = true,
            HasFixedWidthViewport = false,
            Technologies =
            [
                new DetectedTechnology
                {
                    Category = TechnologyCategory.Analytics,
                    Name = "Google Analytics 4",
                    Match = "gtag/js?id=G-"
                },
                new DetectedTechnology
                {
                    Category = TechnologyCategory.Chat,
                    Name = "WhatsApp",
                    Match = "wa.me/"
                }
            ],
            ObservedAt = DateTimeOffset.UtcNow,

            // Medidas da auditoria profunda (0018). O robots.txt desta loja
            // bloqueia dois agentes, e só um deles responde a pergunta do
            // comprador agora — a distinção que separa alarme de diagnóstico.
            AiCrawlersBlocked = ["GPTBot", "OAI-SearchBot"],
            HasLlmsTxt = false,
            IsIndexable = true,
            RawTextWords = 420,
            StructuredDataTypes = ["AutoDealer", "Offer", "Vehicle"],
            StructuredDataHasNap = true,
            H1Count = 1,
            H2Count = 6,
            TitleLength = 58,
            MetaDescriptionLength = 149,
            CanonicalIsSelfReferencing = true,
            ImageCount = 24,
            ImagesWithAlt = 20,
            ImagesWithDimensions = 4,
            ImagesModernFormat = 0,
            HasHsts = true,
            InternalLinkCount = 37,
            DeclaredLanguage = "pt-BR"
        };
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _probe = new StubProbe();

        _services = _postgres.BuildWorkerServices(services =>
            services.AddSingleton<IWebsiteProbe>(_probe));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private async Task<(Guid AccountId, Guid RunId, Guid EventId)> ArrangeAsync(
        string? scenario = null, string? domain = "grupoventosul.com.br")
    {
        var accountId = await TestData.CreateAccountAsync(Get<IAccountRepository>(), "11222333000181");

        if (domain is not null)
        {
            await ExecuteAsync(
                "update accounts set domain = @Domain where id = @Id",
                new { Domain = domain, Id = accountId });
        }

        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        await using var uow = await Get<IUnitOfWorkFactory>().BeginAsync();

        await Get<IResearchRunRepository>().CreateAsync(uow, runId, accountId, "website_audit");

        await Get<IOutboxRepository>().EnqueueAsync(uow, new OutboxEvent
        {
            Id = eventId,
            EventType = EventTypes.AuditRequested,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                research_run_id = runId,
                fixture_scenario = scenario
            }),
            IdempotencyKey = IdempotencyKey.ForAudit(accountId, runId),
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        });

        await uow.CommitAsync();
        return (accountId, runId, eventId);
    }

    [Fact]
    public async Task Slice_completo_persiste_auditoria_evidencias_e_tecnologias()
    {
        var (accountId, runId, _) = await ArrangeAsync();

        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        // A sonda rodou sobre o dominio da conta, normalizado para URL absoluta.
        Assert.Equal("https://grupoventosul.com.br", Assert.Single(_probe.Probed));

        var audit = await QuerySingleAsync<AuditRow>("""
            select id                    as Id,
                   url                   as Url,
                   status                as Status,
                   performance_score     as Performance,
                   seo_score             as Seo,
                   inventory_score       as Inventory,
                   tracking_score        as Tracking,
                   multiple_portals      as MultiplePortals,
                   complex_integration   as ComplexIntegration,
                   probe is not null     as HasProbe,
                   research_run_id       as ResearchRunId
              from website_audits where account_id = @Id
            """, new { Id = accountId });

        Assert.Equal("completed", audit.Status);
        Assert.Equal(runId, audit.ResearchRunId);

        // As quatro notas da sonda existem; as tres do agente tambem, porque o
        // fixture traz vitrine, conversao e achados.
        Assert.NotNull(audit.Performance);
        Assert.NotNull(audit.Seo);
        Assert.NotNull(audit.Inventory);
        Assert.NotNull(audit.Tracking);

        // A medicao crua foi guardada, e nao so o derivado: e o que permite
        // recalcular uma safra antiga quando a formula mudar.
        Assert.True(audit.HasProbe);

        // Dois portais no fixture viram o fato de Technology Pain.
        Assert.True(audit.MultiplePortals);

        // Evidencias ligadas pela TABELA, e nao pelo array que a 0015 removeu.
        var linked = await ScalarAsync<long>(
            "select count(*) from website_audit_evidence where website_audit_id = @Id",
            new { Id = audit.Id });

        Assert.Equal(8, linked);

        // Tecnologias das duas origens: as medidas pela sonda e as inferidas pelo
        // agente. O check da 0015 so deixa passar 'agent' com evidencia, entao a
        // presenca das duas ja prova que a Regra 1 foi satisfeita no banco.
        var probeTech = await ScalarAsync<long>(
            "select count(*) from technologies where account_id = @Id and source = 'probe'",
            new { Id = accountId });

        var agentTech = await ScalarAsync<long>(
            "select count(*) from technologies where account_id = @Id and source = 'agent' and evidence_id is not null",
            new { Id = accountId });

        Assert.Equal(2, probeTech);
        Assert.Equal(3, agentTech);
    }

    /// <summary>
    /// As medidas de AEO e GEO chegam ao banco, e o array sobrevive à ida e
    /// volta pelo Npgsql.
    ///
    /// O achado que este teste protege é o mais acionável da auditoria: a loja
    /// bloqueia dois robôs de IA, e **um deles responde a pergunta do comprador
    /// agora**. `ai_search_crawlers_blocked` é derivado na escrita porque a
    /// classificação de cada agente vive no código, não no banco — recalcular na
    /// consulta exigiria duplicar a taxonomia em SQL.
    /// </summary>
    [Fact]
    public async Task Medidas_de_aeo_e_geo_sao_persistidas()
    {
        var (accountId, _, _) = await ArrangeAsync();

        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var deep = await QuerySingleAsync<DeepRow>("""
            select ai_crawlers_blocked          as AiBlocked,
                   ai_search_crawlers_blocked   as AiSearchBlocked,
                   is_indexable                 as IsIndexable,
                   raw_text_words               as RawTextWords,
                   structured_data_types        as Types,
                   structured_data_has_nap      as HasNap,
                   images_with_dimensions       as ImagesWithDimensions,
                   image_count                  as ImageCount,
                   declared_language            as Lang
              from website_audits where account_id = @Id
            """, new { Id = accountId });

        Assert.Equal(["GPTBot", "OAI-SearchBot"], deep.AiBlocked);

        // Dois bloqueados, um de busca. O número que vira argumento comercial.
        Assert.Equal(1, deep.AiSearchBlocked);

        Assert.True(deep.IsIndexable);
        Assert.Equal(420, deep.RawTextWords);

        // Vehicle e Offer juntos: o estoque é citável por um motor de resposta.
        Assert.Contains("Vehicle", deep.Types!);
        Assert.Contains("Offer", deep.Types!);
        Assert.True(deep.HasNap);

        // 4 de 24 imagens com dimensão declarada — o proxy de CLS que dá para
        // medir sem navegador.
        Assert.Equal(24, deep.ImageCount);
        Assert.Equal(4, deep.ImagesWithDimensions);

        Assert.Equal("pt-BR", deep.Lang);
    }

    private sealed record DeepRow
    {
        public string[]? AiBlocked { get; init; }
        public int? AiSearchBlocked { get; init; }
        public bool? IsIndexable { get; init; }
        public int? RawTextWords { get; init; }
        public string[]? Types { get; init; }
        public bool? HasNap { get; init; }
        public int? ImagesWithDimensions { get; init; }
        public int? ImageCount { get; init; }
        public string? Lang { get; init; }
    }

    /// <summary>
    /// A auditoria alimenta Technology Pain. Antes da 0015, `multiple_portals` e
    /// `complex_integration` nao tinham coluna: o dominio os declarava, o
    /// OpportunityScoring os lia, e o adaptador nao tinha de onde trazer. Este
    /// teste fecha o circuito - da sonda ate o breakdown do score.
    /// </summary>
    [Fact]
    public async Task Auditoria_alimenta_technology_pain_no_score()
    {
        var (accountId, _, _) = await ArrangeAsync();

        // audit.requested -> audit.completed -> (Orchestrator) -> score.requested
        // -> score.ready. O comprimento deixou de ser fixo quando o Orchestrator
        // passou a decidir, entao a espera e pela CONDICAO.
        var scores = Get<IAccountScoreRepository>();

        await TestData.DrainUntilAsync(
            Get<OutboxDispatcher>(),
            async () => await scores.GetCurrentAsync(accountId) is not null,
            TestContext.Current.CancellationToken,
            _postgres.ConnectionString);

        var facts = await Get<IAccountScoreRepository>().LoadFactsAsync(accountId);

        Assert.NotNull(facts!.Audit);
        Assert.True(facts.Audit!.MultiplePortals);
        Assert.NotNull(facts.Audit.PerformanceScore);

        var score = await Get<IAccountScoreRepository>().GetCurrentAsync(accountId);

        Assert.NotNull(score);
        Assert.True(score!.TechnologyPain > 0,
            "Com auditoria persistida, Technology Pain nao pode continuar zerado.");
    }

    /// <summary>
    /// Site fora do ar nao gasta modelo e nao vira sete zeros. Zerar afirmaria
    /// que o site e pessimo, quando o que houve pode ter sido DNS quebrado ou um
    /// dominio errado vindo da pesquisa.
    /// </summary>
    [Fact]
    public async Task Site_inalcancavel_grava_auditoria_sem_notas_e_sem_agent_run_de_modelo()
    {
        _probe.Next = WebsiteProbeResult.Unreachable(
            "https://grupoventosul.com.br", "DNS nao resolveu", DateTimeOffset.UtcNow);

        var (accountId, _, _) = await ArrangeAsync();

        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var audit = await QuerySingleAsync<UnreachableRow>("""
            select status            as Status,
                   performance_score as Performance,
                   tracking_score    as Tracking
              from website_audits where account_id = @Id
            """, new { Id = accountId });

        Assert.Equal("unreachable", audit.Status);
        Assert.Null(audit.Performance);
        Assert.Null(audit.Tracking);

        var evidencias = await ScalarAsync<long>(
            "select count(*) from evidence where account_id = @Id", new { Id = accountId });

        Assert.Equal(0, evidencias);
    }

    /// <summary>
    /// O ciclo de reparo, pelo mesmo caminho de codigo de producao: o fixture
    /// devolve indices de evidencia fora do intervalo, o EvidenceFirstGuard
    /// reprova, e a segunda tentativa passa.
    /// </summary>
    [Fact]
    public async Task Ciclo_de_reparo_recupera_indice_de_evidencia_invalido()
    {
        var (accountId, _, _) = await ArrangeAsync(scenario: "missing-evidence");

        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var status = await ScalarAsync<string>(
            "select status from website_audits where account_id = @Id", new { Id = accountId });

        Assert.Equal("completed", status);

        // Duas chamadas ao agente: a rejeitada e a reparada. As duas viram
        // agent_run, porque o custo de ambas foi incorrido.
        var runs = await ScalarAsync<long>(
            "select count(*) from agent_runs where account_id = @Id and agent_name = 'website-auditor'",
            new { Id = accountId });

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Conta_sem_dominio_falha_o_run_sem_sondar()
    {
        var (accountId, runId, _) = await ArrangeAsync(domain: null);

        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_probe.Probed);

        var status = await ScalarAsync<string>(
            "select status from research_runs where id = @Id", new { Id = runId });

        Assert.Equal(RunStatus.Failed, status);
    }

    // ------------------------------------------------------------ utilitarios

    private sealed record AuditRow
    {
        public Guid Id { get; init; }
        public string Url { get; init; } = "";
        public string Status { get; init; } = "";
        public decimal? Performance { get; init; }
        public decimal? Seo { get; init; }
        public decimal? Inventory { get; init; }
        public decimal? Tracking { get; init; }
        public bool? MultiplePortals { get; init; }
        public bool? ComplexIntegration { get; init; }
        public bool HasProbe { get; init; }
        public Guid? ResearchRunId { get; init; }
    }

    private sealed record UnreachableRow
    {
        public string Status { get; init; } = "";
        public decimal? Performance { get; init; }
        public decimal? Tracking { get; init; }
    }

    /// <summary>
    /// Consulta que, ao nao achar linha, conta POR QUE em vez de estourar com
    /// "Sequence contains no elements".
    ///
    /// O OutboxDispatcher captura toda excecao e a guarda em
    /// <c>events_outbox.last_error</c> antes de reagendar. Sem trazer esse texto
    /// para a mensagem do teste, uma falha do slice aparece como tabela vazia - e
    /// a tabela vazia e o SINTOMA, nunca a causa.
    /// </summary>
    private async Task<T> QuerySingleAsync<T>(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        var row = await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);

        if (row is not null) return row;

        var errors = (await connection.QueryAsync<string>(
            "select coalesce(last_error, '(sem erro)') from events_outbox where last_error is not null"))
            .ToList();

        var runErrors = (await connection.QueryAsync<string>(
            "select error::text from research_runs where error is not null"))
            .ToList();

        throw new InvalidOperationException(
            "A consulta nao retornou linha. Erros registrados: " +
            $"outbox=[{(errors.Count > 0 ? string.Join(" | ", errors) : "nenhum")}] " +
            $"research_runs=[{(runErrors.Count > 0 ? string.Join(" | ", runErrors) : "nenhum")}]");
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql, parameters);
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.ExecuteAsync(sql, parameters);
    }
}
