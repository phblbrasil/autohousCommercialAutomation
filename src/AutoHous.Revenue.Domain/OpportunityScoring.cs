namespace AutoHous.Revenue.Domain;

/// <summary>Um sinal de compra com data — recencia importa no peso.</summary>
public sealed record ScoredSignal(string SignalType, decimal Strength, DateTimeOffset ObservedAt);

/// <summary>
/// O que a auditoria de site observou. Nulo enquanto o Website Auditor nao
/// existir: o score reporta a dimensao como nao observada em vez de inventar.
/// </summary>
public sealed record WebsiteAuditFacts
{
    /// <summary>0 a 1. Menor = pior desempenho = mais dor.</summary>
    public decimal? PerformanceScore { get; init; }

    /// <summary>0 a 1. Menor = pior SEO / landing pages.</summary>
    public decimal? SeoScore { get; init; }

    /// <summary>Estoque publicado em mais de um portal — sintoma de fragmentacao.</summary>
    public bool? MultiplePortals { get; init; }

    /// <summary>Integracao aparente com DMS/ERP/CRM heterogeneos.</summary>
    public bool? ComplexIntegration { get; init; }
}

public sealed record ContactabilityFacts
{
    public bool HasDecisionMaker { get; init; }
    public bool HasProfessionalEmail { get; init; }
    public bool HasCorporatePhone { get; init; }
    public bool HasLinkedIn { get; init; }

    /// <summary>Contatos comprovadamente invalidos (hard bounce, telefone morto).</summary>
    public int InvalidContacts { get; init; }
}

public sealed record ScoringInputs
{
    public required DateTimeOffset ReferenceDate { get; init; }

    public AutomotiveOperation? Operation { get; init; }
    public int? StoreCount { get; init; }
    public int? InventoryEstimate { get; init; }

    /// <summary>Quantos CNPJs a conta agrega. &gt;1 significa grupo economico resolvido.</summary>
    public int CnpjCount { get; init; } = 1;

    public int BrandCount { get; init; }

    /// <summary>Alguma marca com relacionamento de concessionaria autorizada.</summary>
    public bool HasAuthorizedBrand { get; init; }

    public IReadOnlyList<ScoredSignal> Signals { get; init; } = [];
    public WebsiteAuditFacts? Audit { get; init; }
    public ContactabilityFacts Contacts { get; init; } = new();
}

/// <summary>
/// Uma linha do breakdown. <c>Observed=false</c> significa "ainda nao sabemos",
/// que e diferente de "vale zero" — e a diferenca que decide se vale a pena
/// pesquisar mais ou descartar a conta.
/// </summary>
public sealed record ScoreComponent(
    string Dimension,
    string Criterion,
    decimal Points,
    decimal MaxPoints,
    bool Observed,
    string Rationale);

public sealed record OpportunityScore
{
    public required decimal CompanyFit { get; init; }
    public required decimal TechnologyPain { get; init; }
    public required decimal BuyingSignal { get; init; }
    public required decimal Contactability { get; init; }
    public required IReadOnlyList<ScoreComponent> Breakdown { get; init; }

    public decimal Total => Math.Round(
        Math.Clamp(CompanyFit + TechnologyPain + BuyingSignal + Contactability, 0m, 100m), 2);

    public string Band => Total switch
    {
        >= 85m => "hot",
        >= 70m => "high",
        >= 50m => "medium",
        _ => "nurture"
    };

    public short Tier => Total switch
    {
        >= 85m => (short)1,
        >= 70m => (short)2,
        >= 50m => (short)3,
        _ => (short)4
    };

    /// <summary>
    /// Fracao dos pontos possiveis que veio de fato observado. Um score de 55
    /// com 40% de cobertura e um pedido de mais pesquisa, nao um veredito.
    /// </summary>
    public decimal Coverage
    {
        get
        {
            var total = Breakdown.Sum(c => c.MaxPoints);
            if (total == 0) return 0m;

            return Math.Round(Breakdown.Where(c => c.Observed).Sum(c => c.MaxPoints) / total, 4);
        }
    }
}

/// <summary>
/// Opportunity Score do frame 06: 30 / 30 / 25 / 15.
///
/// Deterministico e puro de proposito. O score prioriza a fila de execucao e
/// precisa ser reproduzivel: dois calculos sobre os mesmos fatos tem que dar o
/// mesmo numero, sempre. Um LLM aqui tornaria impossivel responder "por que esta
/// conta caiu de 82 para 68?".
///
/// O papel do modelo, quando entrar, e produzir os FATOS (auditoria, sinais,
/// contatos) — nunca a aritmetica.
/// </summary>
public static class OpportunityScoring
{
    public const string Version = "opportunity-score-v1";

    /// <summary>Sinal mais novo que isto vale peso cheio.</summary>
    private static readonly TimeSpan FullWeightWindow = TimeSpan.FromDays(90);

    /// <summary>A partir daqui o sinal nao conta mais.</summary>
    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromDays(365);

    public static OpportunityScore Calculate(ScoringInputs inputs)
    {
        var components = new List<ScoreComponent>();

        components.AddRange(CompanyFit(inputs));
        components.AddRange(TechnologyPain(inputs));
        components.AddRange(BuyingSignal(inputs));
        components.AddRange(Contactability(inputs));

        decimal Sum(string dimension) => Math.Round(
            components.Where(c => c.Dimension == dimension).Sum(c => c.Points), 2);

        return new OpportunityScore
        {
            CompanyFit = Sum(Dimensions.CompanyFit),
            TechnologyPain = Sum(Dimensions.TechnologyPain),
            BuyingSignal = Sum(Dimensions.BuyingSignal),
            Contactability = Sum(Dimensions.Contactability),
            Breakdown = components
        };
    }

    public static class Dimensions
    {
        public const string CompanyFit = "company_fit";
        public const string TechnologyPain = "technology_pain";
        public const string BuyingSignal = "buying_signal";
        public const string Contactability = "contactability";
    }

    // ------------------------------------------------------------ 30 pontos

    private static IEnumerable<ScoreComponent> CompanyFit(ScoringInputs i)
    {
        // CNAE / tipo de operacao — 5
        var operationPoints = i.Operation switch
        {
            AutomotiveOperation.Concessionaria => 5m,
            AutomotiveOperation.Revenda => 4m,
            AutomotiveOperation.Atacado => 3m,
            AutomotiveOperation.Motos => 2m,
            AutomotiveOperation.Intermediacao => 2m,
            AutomotiveOperation.Locadora => 1m,
            _ => 0m
        };

        yield return new ScoreComponent(
            Dimensions.CompanyFit, "operacao", operationPoints, 5m, i.Operation is not null,
            i.Operation is null ? "CNAE nao classificado" : $"operacao {CnaeCatalog.ToSegment(i.Operation.Value)}");

        // Numero de lojas — 10. E o preditor mais forte de dor de presenca
        // digital: multi-loja quebra site, estoque e atendimento ao mesmo tempo.
        var stores = i.StoreCount;
        var storePoints = stores switch
        {
            null => 0m,
            >= 10 => 10m,
            >= 5 => 8m,
            >= 3 => 6m,
            2 => 4m,
            1 => 2m,
            _ => 0m
        };

        yield return new ScoreComponent(
            Dimensions.CompanyFit, "lojas", storePoints, 10m, stores is not null,
            stores is null ? "contagem de lojas desconhecida" : $"{stores} loja(s)");

        // Estoque / escala — 5
        var inventory = i.InventoryEstimate;
        var inventoryPoints = inventory switch
        {
            null => 0m,
            >= 500 => 5m,
            >= 200 => 4m,
            >= 80 => 3m,
            >= 30 => 2m,
            > 0 => 1m,
            _ => 0m
        };

        yield return new ScoreComponent(
            Dimensions.CompanyFit, "estoque", inventoryPoints, 5m, inventory is not null,
            inventory is null ? "estoque nao estimado" : $"~{inventory} veiculo(s)");

        // Grupo economico — 5. Sempre observado: a contagem de CNPJs vem do
        // proprio account graph, nao de pesquisa externa.
        var groupPoints = i.CnpjCount switch
        {
            >= 5 => 5m,
            >= 3 => 4m,
            2 => 3m,
            _ => 0m
        };

        yield return new ScoreComponent(
            Dimensions.CompanyFit, "grupo_economico", groupPoints, 5m, true,
            $"{i.CnpjCount} CNPJ(s) na conta");

        // Marca / concessionaria — 5
        var brandPoints = i.HasAuthorizedBrand
            ? 5m
            : i.BrandCount switch { >= 3 => 4m, 2 => 3m, 1 => 2m, _ => 0m };

        yield return new ScoreComponent(
            Dimensions.CompanyFit, "marcas", brandPoints, 5m, i.BrandCount > 0 || i.HasAuthorizedBrand,
            i.HasAuthorizedBrand ? "concessionaria autorizada" : $"{i.BrandCount} marca(s)");
    }

    // ------------------------------------------------------------ 30 pontos

    private static IEnumerable<ScoreComponent> TechnologyPain(ScoringInputs i)
    {
        var audit = i.Audit;

        // Site / UX / performance — 10. Dor observada, nunca hipotese: sem
        // auditoria a dimensao inteira fica em zero e marcada como nao observada.
        var performance = audit?.PerformanceScore;

        yield return new ScoreComponent(
            Dimensions.TechnologyPain, "performance",
            performance is null ? 0m : Math.Round((1m - Math.Clamp(performance.Value, 0m, 1m)) * 10m, 2),
            10m, performance is not null,
            performance is null ? "sem auditoria de site" : $"performance {performance:P0}");

        var seo = audit?.SeoScore;

        yield return new ScoreComponent(
            Dimensions.TechnologyPain, "seo",
            seo is null ? 0m : Math.Round((1m - Math.Clamp(seo.Value, 0m, 1m)) * 5m, 2),
            5m, seo is not null,
            seo is null ? "sem auditoria de SEO" : $"SEO {seo:P0}");

        yield return new ScoreComponent(
            Dimensions.TechnologyPain, "multiplos_portais",
            audit?.MultiplePortals == true ? 5m : 0m,
            5m, audit?.MultiplePortals is not null,
            audit?.MultiplePortals switch
            {
                true => "estoque em mais de um portal",
                false => "portal unico",
                _ => "portais nao verificados"
            });

        // Multiplas lojas — 5. Deriva do mesmo fato que ja pontuou em Company
        // Fit, mas aqui mede outra coisa: a dor operacional de manter varias
        // vitrines coerentes. O frame 06 lista o criterio nas duas dimensoes.
        var multiStore = i.StoreCount is > 1;

        yield return new ScoreComponent(
            Dimensions.TechnologyPain, "multiplas_lojas",
            i.StoreCount switch { null => 0m, >= 5 => 5m, >= 3 => 4m, 2 => 3m, _ => 0m },
            5m, i.StoreCount is not null,
            multiStore ? "operacao multi-loja" : "loja unica ou desconhecida");

        yield return new ScoreComponent(
            Dimensions.TechnologyPain, "integracao_complexa",
            audit?.ComplexIntegration == true ? 5m : 0m,
            5m, audit?.ComplexIntegration is not null,
            audit?.ComplexIntegration switch
            {
                true => "integracoes heterogeneas aparentes",
                false => "sem indicio de fragmentacao",
                _ => "integracoes nao verificadas"
            });
    }

    // ------------------------------------------------------------ 25 pontos

    private static IEnumerable<ScoreComponent> BuyingSignal(ScoringInputs i)
    {
        // Cada familia de sinal vale ate 5. Um grupo que anunciou tres unidades
        // novas nao vale 15 pontos de "expansao": vale 5, e os outros 20 tem que
        // vir de tipos de sinal diferentes. Sem esse teto, um unico evento
        // repetido em varias fontes inflaria o score inteiro.
        var families = new (string Key, string Label, string[] Match)[]
        {
            ("expansao",   "expansao / nova loja",      ["expansion", "new_store", "expansao", "nova_loja"]),
            ("bandeira",   "nova bandeira / marca",     ["new_brand", "brand", "bandeira", "marca"]),
            ("lideranca",  "mudanca de lideranca",      ["leadership", "hire_exec", "lideranca"]),
            ("vaga",       "vaga de TI / marketing",    ["job_posting", "hiring", "vaga"]),
            ("replatform", "site antigo / replatform",  ["replatform", "website_change", "migracao", "rebranding"])
        };

        var observed = i.Signals.Count > 0;

        foreach (var (key, label, match) in families)
        {
            var best = i.Signals
                .Where(s => match.Any(m => s.SignalType.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .Select(s => new
                {
                    Signal = s,
                    Value = 5m * Math.Clamp(s.Strength, 0m, 1m) * Recency(s.ObservedAt, i.ReferenceDate)
                })
                .OrderByDescending(x => x.Value)
                .FirstOrDefault();

            yield return new ScoreComponent(
                Dimensions.BuyingSignal, key,
                best is null ? 0m : Math.Round(best.Value, 2),
                5m, observed,
                best is null
                    ? $"sem sinal de {label}"
                    : $"{label}: forca {best.Signal.Strength:0.00}, observado em {best.Signal.ObservedAt:yyyy-MM-dd}");
        }
    }

    /// <summary>
    /// Peso por recencia. Cheio ate 90 dias, decaimento linear ate zerar em um
    /// ano. "O grupo abriu uma loja" e sinal de compra em marco e ruido em
    /// dezembro — usar sinal velho como gancho de abordagem produz exatamente a
    /// mensagem que o guardrail de grounding existe para evitar.
    /// </summary>
    private static decimal Recency(DateTimeOffset observedAt, DateTimeOffset reference)
    {
        var age = reference - observedAt;

        if (age <= FullWeightWindow) return 1m;
        if (age >= ExpiryWindow) return 0m;

        var decayed = (decimal)((ExpiryWindow - age).TotalDays / (ExpiryWindow - FullWeightWindow).TotalDays);
        return Math.Clamp(decayed, 0m, 1m);
    }

    // ------------------------------------------------------------ 15 pontos

    private static IEnumerable<ScoreComponent> Contactability(ScoringInputs i)
    {
        var c = i.Contacts;
        var anything = c.HasDecisionMaker || c.HasProfessionalEmail || c.HasCorporatePhone || c.HasLinkedIn;

        yield return new ScoreComponent(
            Dimensions.Contactability, "decisor", c.HasDecisionMaker ? 5m : 0m, 5m, anything,
            c.HasDecisionMaker ? "decisor identificado" : "sem decisor identificado");

        yield return new ScoreComponent(
            Dimensions.Contactability, "email", c.HasProfessionalEmail ? 5m : 0m, 5m, anything,
            c.HasProfessionalEmail ? "email profissional" : "sem email profissional");

        yield return new ScoreComponent(
            Dimensions.Contactability, "telefone", c.HasCorporatePhone ? 3m : 0m, 3m, anything,
            c.HasCorporatePhone ? "telefone corporativo" : "sem telefone corporativo");

        yield return new ScoreComponent(
            Dimensions.Contactability, "linkedin", c.HasLinkedIn ? 2m : 0m, 2m, anything,
            c.HasLinkedIn ? "perfil localizado" : "sem perfil localizado");

        // Penalidade do frame 06: "contato invalido = penalidade". Nao pode
        // derrubar as outras dimensoes, entao o piso da dimensao e zero.
        if (c.InvalidContacts > 0)
        {
            var penalty = Math.Min(c.InvalidContacts * 2m, 15m);
            var earned =
                (c.HasDecisionMaker ? 5m : 0m) + (c.HasProfessionalEmail ? 5m : 0m) +
                (c.HasCorporatePhone ? 3m : 0m) + (c.HasLinkedIn ? 2m : 0m);

            yield return new ScoreComponent(
                Dimensions.Contactability, "contatos_invalidos",
                -Math.Min(penalty, earned), 0m, true,
                $"{c.InvalidContacts} contato(s) invalido(s)");
        }
    }
}
