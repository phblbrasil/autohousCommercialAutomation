namespace AutoHous.Revenue.Domain.Tests;

public class OpportunityScoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    private static ScoringInputs Inputs(Action<ScoringInputsBuilder>? configure = null)
    {
        var builder = new ScoringInputsBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private sealed class ScoringInputsBuilder
    {
        public AutomotiveOperation? Operation { get; set; }
        public int? StoreCount { get; set; }
        public int? InventoryEstimate { get; set; }
        public int CnpjCount { get; set; } = 1;
        public int BrandCount { get; set; }
        public bool HasAuthorizedBrand { get; set; }
        public List<ScoredSignal> Signals { get; } = [];
        public WebsiteAuditFacts? Audit { get; set; }
        public ContactabilityFacts Contacts { get; set; } = new();

        public ScoringInputs Build() => new()
        {
            ReferenceDate = Now,
            Operation = Operation,
            StoreCount = StoreCount,
            InventoryEstimate = InventoryEstimate,
            CnpjCount = CnpjCount,
            BrandCount = BrandCount,
            HasAuthorizedBrand = HasAuthorizedBrand,
            Signals = Signals,
            Audit = Audit,
            Contacts = Contacts
        };
    }

    // ------------------------------------------------------------- estrutura

    [Fact]
    public void Conta_sem_nenhum_fato_pontua_zero_e_cai_em_nurture()
    {
        var score = OpportunityScoring.Calculate(Inputs());

        Assert.Equal(0m, score.Total);
        Assert.Equal("nurture", score.Band);
        Assert.Equal((short)4, score.Tier);
    }

    [Fact]
    public void Dimensoes_respeitam_o_teto_de_30_30_25_15()
    {
        var maxed = Inputs(b =>
        {
            b.Operation = AutomotiveOperation.Concessionaria;
            b.StoreCount = 40;
            b.InventoryEstimate = 5000;
            b.CnpjCount = 12;
            b.HasAuthorizedBrand = true;
            b.BrandCount = 6;
            b.Audit = new WebsiteAuditFacts
            {
                PerformanceScore = 0m,
                SeoScore = 0m,
                MultiplePortals = true,
                ComplexIntegration = true
            };
            b.Contacts = new ContactabilityFacts
            {
                HasDecisionMaker = true,
                HasProfessionalEmail = true,
                HasCorporatePhone = true,
                HasLinkedIn = true
            };

            foreach (var type in new[] { "expansion", "new_brand", "leadership_change", "job_posting", "replatform" })
            {
                b.Signals.Add(new ScoredSignal(type, 1.0m, Now.AddDays(-10)));
            }
        });

        var score = OpportunityScoring.Calculate(maxed);

        Assert.Equal(30m, score.CompanyFit);
        Assert.Equal(30m, score.TechnologyPain);
        Assert.Equal(25m, score.BuyingSignal);
        Assert.Equal(15m, score.Contactability);
        Assert.Equal(100m, score.Total);
        Assert.Equal("hot", score.Band);
        Assert.Equal((short)1, score.Tier);
    }

    [Theory]
    [InlineData(90, "hot", 1)]
    [InlineData(85, "hot", 1)]
    [InlineData(84, "high", 2)]
    [InlineData(70, "high", 2)]
    [InlineData(69, "medium", 3)]
    [InlineData(50, "medium", 3)]
    [InlineData(49, "nurture", 4)]
    public void Faixas_e_tiers_seguem_o_frame_06(int total, string band, int tier)
    {
        var score = new OpportunityScore
        {
            CompanyFit = total,
            TechnologyPain = 0,
            BuyingSignal = 0,
            Contactability = 0,
            Breakdown = []
        };

        Assert.Equal(band, score.Band);
        Assert.Equal((short)tier, score.Tier);
    }

    // -------------------------------------------- observado versus inexistente

    /// <summary>
    /// Sem auditoria de site, Technology Pain vale zero — mas marcado como NAO
    /// observado. A diferenca entre "site bom" e "nao olhamos o site" decide se
    /// vale pesquisar mais ou descartar a conta.
    /// </summary>
    [Fact]
    public void Dimensao_sem_dado_e_reportada_como_nao_observada()
    {
        var score = OpportunityScoring.Calculate(Inputs(b => b.Operation = AutomotiveOperation.Revenda));

        var performance = score.Breakdown.Single(c => c.Criterion == "performance");

        Assert.Equal(0m, performance.Points);
        Assert.False(performance.Observed);
        Assert.Contains("sem auditoria", performance.Rationale);
    }

    [Fact]
    public void Cobertura_cresce_conforme_os_fatos_chegam()
    {
        var vazio = OpportunityScoring.Calculate(Inputs());

        var comPesquisa = OpportunityScoring.Calculate(Inputs(b =>
        {
            b.Operation = AutomotiveOperation.Concessionaria;
            b.StoreCount = 8;
            b.InventoryEstimate = 300;
            b.BrandCount = 3;
            b.Signals.Add(new ScoredSignal("expansion", 0.8m, Now.AddDays(-20)));
        }));

        Assert.True(comPesquisa.Coverage > vazio.Coverage);
        Assert.True(comPesquisa.Coverage < 1m); // auditoria e contatos ainda faltam
    }

    /// <summary>
    /// Site bom e site nao auditado produzem o mesmo zero de pontos, mas nao a
    /// mesma cobertura — e a cobertura e o que distingue os dois.
    /// </summary>
    [Fact]
    public void Site_bom_e_site_nao_auditado_diferem_na_cobertura()
    {
        var auditado = OpportunityScoring.Calculate(Inputs(b =>
            b.Audit = new WebsiteAuditFacts { PerformanceScore = 1.0m, SeoScore = 1.0m }));

        var naoAuditado = OpportunityScoring.Calculate(Inputs());

        Assert.Equal(naoAuditado.TechnologyPain, auditado.TechnologyPain);
        Assert.True(auditado.Coverage > naoAuditado.Coverage);
    }

    // ----------------------------------------------------------- pain e sinal

    [Fact]
    public void Site_pior_gera_mais_pontos_de_dor()
    {
        decimal Pain(decimal performance) => OpportunityScoring
            .Calculate(Inputs(b => b.Audit = new WebsiteAuditFacts { PerformanceScore = performance }))
            .TechnologyPain;

        Assert.True(Pain(0.2m) > Pain(0.9m));
        Assert.Equal(10m, Pain(0m));
        Assert.Equal(0m, Pain(1m));
    }

    [Fact]
    public void Sinal_recente_vale_peso_cheio()
    {
        var score = OpportunityScoring.Calculate(Inputs(b =>
            b.Signals.Add(new ScoredSignal("expansion", 1.0m, Now.AddDays(-30)))));

        Assert.Equal(5m, score.BuyingSignal);
    }

    [Fact]
    public void Sinal_antigo_decai_e_expira()
    {
        decimal Signal(int daysAgo) => OpportunityScoring
            .Calculate(Inputs(b => b.Signals.Add(new ScoredSignal("expansion", 1.0m, Now.AddDays(-daysAgo)))))
            .BuyingSignal;

        Assert.Equal(5m, Signal(89));
        Assert.True(Signal(200) is > 0m and < 5m);
        Assert.Equal(0m, Signal(400));
    }

    /// <summary>
    /// Um mesmo evento noticiado em tres portais nao vale 15 pontos. Sem o teto
    /// por familia, uma cobertura de imprensa inflaria a dimensao inteira.
    /// </summary>
    [Fact]
    public void Sinais_da_mesma_familia_nao_somam_alem_do_teto()
    {
        var score = OpportunityScoring.Calculate(Inputs(b =>
        {
            b.Signals.Add(new ScoredSignal("expansion", 1.0m, Now.AddDays(-5)));
            b.Signals.Add(new ScoredSignal("expansion_news", 1.0m, Now.AddDays(-6)));
            b.Signals.Add(new ScoredSignal("nova_loja", 1.0m, Now.AddDays(-7)));
        }));

        Assert.Equal(5m, score.BuyingSignal);
    }

    [Fact]
    public void Familias_diferentes_de_sinal_somam()
    {
        var score = OpportunityScoring.Calculate(Inputs(b =>
        {
            b.Signals.Add(new ScoredSignal("expansion", 1.0m, Now.AddDays(-5)));
            b.Signals.Add(new ScoredSignal("new_brand", 1.0m, Now.AddDays(-5)));
        }));

        Assert.Equal(10m, score.BuyingSignal);
    }

    [Fact]
    public void Forca_do_sinal_escala_os_pontos()
    {
        var score = OpportunityScoring.Calculate(Inputs(b =>
            b.Signals.Add(new ScoredSignal("expansion", 0.5m, Now.AddDays(-5)))));

        Assert.Equal(2.5m, score.BuyingSignal);
    }

    // ------------------------------------------------------- contactabilidade

    [Fact]
    public void Contato_invalido_penaliza_sem_derrubar_abaixo_de_zero()
    {
        var score = OpportunityScoring.Calculate(Inputs(b =>
        {
            b.Operation = AutomotiveOperation.Concessionaria;
            b.Contacts = new ContactabilityFacts { HasProfessionalEmail = true, InvalidContacts = 10 };
        }));

        Assert.Equal(0m, score.Contactability);
        Assert.True(score.Total > 0m); // as outras dimensoes seguem intactas
    }

    [Fact]
    public void Penalidade_nao_ultrapassa_o_que_foi_ganho()
    {
        var score = OpportunityScoring.Calculate(Inputs(b => b.Contacts =
            new ContactabilityFacts { InvalidContacts = 5 }));

        Assert.Equal(0m, score.Contactability);
    }

    // ---------------------------------------------------------- reprodutibilidade

    [Fact]
    public void Mesmos_fatos_produzem_o_mesmo_numero()
    {
        // O score prioriza a fila de execucao: "por que esta conta caiu de 82
        // para 68?" so tem resposta se o calculo for deterministico.
        ScoringInputs Facts() => Inputs(b =>
        {
            b.Operation = AutomotiveOperation.Concessionaria;
            b.StoreCount = 8;
            b.CnpjCount = 3;
            b.BrandCount = 2;
            b.Signals.Add(new ScoredSignal("expansion", 0.8m, Now.AddDays(-40)));
        });

        var first = OpportunityScoring.Calculate(Facts());
        var second = OpportunityScoring.Calculate(Facts());

        Assert.Equal(first.Total, second.Total);
        Assert.Equal(first.Breakdown.Count, second.Breakdown.Count);
    }

    [Fact]
    public void Breakdown_explica_cada_ponto_atribuido()
    {
        var score = OpportunityScoring.Calculate(Inputs(b =>
        {
            b.Operation = AutomotiveOperation.Concessionaria;
            b.StoreCount = 8;
            b.CnpjCount = 4;
        }));

        Assert.All(score.Breakdown, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Dimension));
            Assert.False(string.IsNullOrWhiteSpace(c.Criterion));
            Assert.False(string.IsNullOrWhiteSpace(c.Rationale));
        });

        var lojas = score.Breakdown.Single(c =>
            c.Dimension == OpportunityScoring.Dimensions.CompanyFit && c.Criterion == "lojas");

        Assert.Equal(8m, lojas.Points);
        Assert.Contains("8 loja(s)", lojas.Rationale);
    }

    [Fact]
    public void Grupo_economico_resolvido_pontua_mais_que_cnpj_isolado()
    {
        decimal Fit(int cnpjs) => OpportunityScoring
            .Calculate(Inputs(b => b.CnpjCount = cnpjs))
            .CompanyFit;

        // Principio de desenho numero 1 da V2: account > CNPJ.
        Assert.True(Fit(5) > Fit(2));
        Assert.True(Fit(2) > Fit(1));
    }
}
