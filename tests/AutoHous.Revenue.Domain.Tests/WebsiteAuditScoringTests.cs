using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Domain.Tests;

/// <summary>
/// O que estes testes protegem nao e a aritmetica - e a DIVISAO DE TRABALHO
/// entre a sonda e o agente, e a distincao entre "nao observado" e "zero".
///
/// As duas coisas sao invisiveis num numero final. Um score 42 nao conta se veio
/// de um site ruim ou de uma auditoria rasa, e e essa diferenca que decide entre
/// descartar a conta e pesquisar mais.
/// </summary>
public class WebsiteAuditScoringTests
{
    private static WebsiteProbeResult Probe(Action<ProbeBuilder>? configure = null)
    {
        var builder = new ProbeBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private sealed class ProbeBuilder
    {
        public int? Status { get; set; } = 200;
        public TimeSpan? Ttfb { get; set; } = TimeSpan.FromMilliseconds(150);
        public long? Bytes { get; set; } = 80_000;
        public int? Blocking { get; set; } = 0;
        public bool? Compression { get; set; } = true;
        public bool? Https { get; set; } = true;
        public bool? Title { get; set; } = true;
        public bool? Viewport { get; set; } = true;
        public bool? FixedWidth { get; set; } = false;
        public List<DetectedTechnology> Technologies { get; } = [];

        public WebsiteProbeResult Build() => new()
        {
            RequestedUrl = "https://exemplo.com.br",
            StatusCode = Status,
            TimeToFirstByte = Ttfb,
            DocumentBytes = Bytes,
            RenderBlockingResources = Blocking,
            CompressionEnabled = Compression,
            IsHttps = Https,
            HasTitle = Title,
            HasViewportMeta = Viewport,
            HasFixedWidthViewport = FixedWidth,
            Technologies = Technologies,
            ObservedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z")
        };
    }

    /// <summary>
    /// Site fora do ar NAO vira sete zeros. Zerar seria afirmar que o site e
    /// pessimo, quando o que houve pode ter sido DNS quebrado, um WAF barrando a
    /// sonda ou um dominio errado vindo da pesquisa - e um score baixo por
    /// dominio errado empurra a conta para o fim da fila sem que ninguem
    /// descubra por que.
    /// </summary>
    [Fact]
    public void Site_inalcancavel_nao_produz_nota_nenhuma()
    {
        var score = WebsiteAuditScoring.Calculate(
            WebsiteProbeResult.Unreachable("https://morto.com.br", "DNS nao resolveu", DateTimeOffset.UtcNow));

        Assert.False(score.Reachable);
        Assert.Null(score.Performance);
        Assert.Null(score.Seo);
        Assert.Null(score.Tracking);
        Assert.Equal(0m, score.Coverage);
        Assert.Contains("DNS", score.Notes.Single());
    }

    /// <summary>
    /// Sem o agente, as dimensoes de julgamento ficam NULAS - e nao zeradas. A
    /// sonda mede quatro das sete; as outras tres nao sao dela.
    /// </summary>
    [Fact]
    public void Sonda_sozinha_pontua_so_o_que_mediu()
    {
        var score = WebsiteAuditScoring.Calculate(Probe());

        Assert.NotNull(score.Performance);
        Assert.NotNull(score.Seo);
        Assert.NotNull(score.Mobile);
        Assert.NotNull(score.Tracking);

        Assert.Null(score.Ux);
        Assert.Null(score.Conversion);
        Assert.Null(score.Inventory);

        Assert.Equal(Math.Round(4m / 7m, 4), score.Coverage);
    }

    /// <summary>
    /// Rastreio ausente e a dor mais direta do catalogo: sem analytics nem tag
    /// manager a empresa nao sabe de onde vem lead nenhum. Tracking e a unica
    /// dimensao da sonda que nunca e nula - ausencia de pixel E a medicao.
    /// </summary>
    [Fact]
    public void Ausencia_de_rastreio_e_medicao_e_nao_ausencia_de_dado()
    {
        var semPixel = WebsiteAuditScoring.Calculate(Probe());
        Assert.Equal(0m, semPixel.Tracking);

        var comPixel = WebsiteAuditScoring.Calculate(Probe(p =>
        {
            p.Technologies.Add(new DetectedTechnology
            {
                Category = TechnologyCategory.Analytics, Name = "GA4", Match = "gtag/js?id=G-"
            });
            p.Technologies.Add(new DetectedTechnology
            {
                Category = TechnologyCategory.TagManager, Name = "GTM", Match = "GTM-"
            });
        }));

        Assert.True(comPixel.Tracking > semPixel.Tracking);
    }

    /// <summary>
    /// Viewport de largura fixa e PIOR que viewport nenhum na experiencia real:
    /// declara suporte a mobile e entrega uma pagina que exige zoom. Mas nao e
    /// pior na nota - a ausencia total continua sendo o piso.
    /// </summary>
    [Fact]
    public void Viewport_de_largura_fixa_pontua_entre_o_ausente_e_o_responsivo()
    {
        var ausente = WebsiteAuditScoring.Calculate(Probe(p => p.Viewport = false)).Mobile;
        var fixo = WebsiteAuditScoring.Calculate(Probe(p => p.FixedWidth = true)).Mobile;
        var responsivo = WebsiteAuditScoring.Calculate(Probe()).Mobile;

        Assert.True(ausente < fixo);
        Assert.True(fixo < responsivo);
    }

    [Fact]
    public void Tempo_de_resposta_alto_derruba_a_performance()
    {
        var rapido = WebsiteAuditScoring.Calculate(Probe()).Performance;
        var lento = WebsiteAuditScoring.Calculate(
            Probe(p => p.Ttfb = TimeSpan.FromSeconds(3))).Performance;

        Assert.True(lento < rapido);
    }

    /// <summary>
    /// Um portal alem do site proprio ja e fragmentacao. Um so - o proprio site
    /// listado - nao e.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Multiplos_portais_conta_a_partir_do_segundo(int portais, bool esperado)
    {
        var profile = ProfileWith(portals: portais);
        var score = WebsiteAuditScoring.Calculate(Probe(), profile);

        Assert.Equal(esperado, score.MultiplePortals);
    }

    /// <summary>
    /// Integracao complexa conta CATEGORIAS distintas, e nao sistemas. Duas
    /// ferramentas de analytics sao redundancia; um DMS, um CRM e uma plataforma
    /// de estoque de fornecedores diferentes sao tres contratos e nenhum dado que
    /// fecha - que e a dor de verdade.
    /// </summary>
    [Fact]
    public void Integracao_complexa_conta_categorias_e_nao_sistemas()
    {
        var tresDaMesmaCategoria = WebsiteAuditScoring.Calculate(Probe(p =>
        {
            for (var i = 0; i < 3; i++)
            {
                p.Technologies.Add(new DetectedTechnology
                {
                    Category = TechnologyCategory.Analytics, Name = $"Ferramenta{i}", Match = "x"
                });
            }
        }));

        Assert.False(tresDaMesmaCategoria.ComplexIntegration);

        var tresCategorias = WebsiteAuditScoring.Calculate(Probe(p =>
        {
            p.Technologies.Add(new DetectedTechnology
            {
                Category = TechnologyCategory.Crm, Name = "RD Station", Match = "rdstation"
            });
            p.Technologies.Add(new DetectedTechnology
            {
                Category = TechnologyCategory.Dms, Name = "Syonet", Match = "syonet"
            });
            p.Technologies.Add(new DetectedTechnology
            {
                Category = TechnologyCategory.InventoryPlatform, Name = "Autoforce", Match = "autoforce"
            });
        }));

        Assert.True(tresCategorias.ComplexIntegration);
    }

    /// <summary>
    /// Vitrine ausente e zero, e nao nulo: aqui a ausencia FOI observada. E a
    /// contrapartida do teste do site inalcancavel - a mesma distincao, do outro
    /// lado.
    /// </summary>
    [Fact]
    public void Vitrine_ausente_e_zero_observado_e_nao_dimensao_nula()
    {
        var profile = ProfileWith(publishedOnline: false);
        var score = WebsiteAuditScoring.Calculate(Probe(), profile);

        Assert.Equal(0m, score.Inventory);
    }

    /// <summary>
    /// Auditoria rasa nao vira nota cheia por falta de achado. Sem esta regra,
    /// um agente que desistiu na primeira pagina produziria ux=100 - "nao
    /// encontrei problemas" viraria "nao ha problemas".
    /// </summary>
    [Fact]
    public void Area_sem_achado_em_auditoria_rasa_fica_nao_observada()
    {
        var rasa = ProfileWith(completeness: 0.3m);
        Assert.Null(WebsiteAuditScoring.Calculate(Probe(), rasa).Ux);

        var completa = ProfileWith(completeness: 0.9m);
        Assert.Equal(100m, WebsiteAuditScoring.Calculate(Probe(), completa).Ux);
    }

    [Fact]
    public void Gravidade_do_achado_desconta_da_area_correspondente()
    {
        var comProblemaGrave = ProfileWith(completeness: 0.9m, issues:
        [
            new AuditIssue
            {
                Area = AuditArea.Ux, Severity = "high",
                Title = "Menu nao abre no celular", EvidenceIndex = 0
            }
        ]);

        var score = WebsiteAuditScoring.Calculate(Probe(), comProblemaGrave);

        Assert.Equal(75m, score.Ux);

        // O desconto e por AREA: um problema de UX nao pode contaminar conversao.
        Assert.NotEqual(75m, score.Conversion);
    }

    private static WebsiteAuditProfile ProfileWith(
        int portals = 0,
        bool publishedOnline = true,
        decimal completeness = 0.8m,
        IReadOnlyList<AuditIssue>? issues = null) => new()
    {
        Summary = "Auditoria de exemplo com texto suficiente para o contrato.",
        AuditedUrl = "https://exemplo.com.br",
        AuditCompleteness = completeness,
        Evidence =
        [
            new EvidenceClaim
            {
                ClaimType = "inventory_count",
                ClaimText = "Vitrine com paginacao visivel.",
                Confidence = 0.8m,
                Source = new SourceRef
                {
                    Type = "website",
                    Url = "https://exemplo.com.br/estoque",
                    ObservedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z")
                }
            }
        ],
        Inventory = new InventoryClaim { PublishedOnline = publishedOnline, EvidenceIndex = 0 },
        Portals = [.. Enumerable.Range(0, portals).Select(i => new PortalClaim
        {
            Name = $"Portal {i}", EvidenceIndex = 0
        })],
        Issues = issues ?? []
    };
}
