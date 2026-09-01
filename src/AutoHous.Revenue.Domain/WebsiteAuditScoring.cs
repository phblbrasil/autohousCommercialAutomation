using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Domain;

/// <summary>
/// As sete notas de <c>website_audits</c>, calculadas sobre a medicao da sonda e
/// as observacoes do agente.
///
/// Deterministico pelo mesmo motivo do <see cref="OpportunityScoring"/>, e o
/// ADR-0005 cobre os dois: o auditor alimenta Technology Pain, que prioriza a
/// fila de execucao. Se a nota de desempenho variasse entre duas execucoes sobre
/// a mesma pagina, "por que esta conta caiu de 82 para 68?" deixaria de ter
/// resposta - e a resposta e o produto.
///
/// A divisao de quem responde o que nao e arbitraria:
///
///   performance, seo, mobile, tracking   SONDA    medida, o agente nem opina
///   ux, conversion, inventory            AGENTE   julgamento, com evidencia
///
/// Nota alta = site bom. A conversao para "dor" (onde alto = ruim) e do
/// adaptador que alimenta o Opportunity Score, nao daqui.
/// </summary>
public static class WebsiteAuditScoring
{
    public const string Version = "website-audit-v1";

    public static WebsiteAuditScore Calculate(
        WebsiteProbeResult probe, WebsiteAuditProfile? profile = null)
    {
        // Site que nao respondeu nao tem nota nenhuma. Zerar as sete seria
        // afirmar que o site e pessimo, quando o que houve foi DNS quebrado, um
        // WAF barrando a sonda ou um dominio errado vindo da pesquisa - e um
        // score baixo por dominio errado empurra a conta para o fim da fila sem
        // que ninguem descubra por que.
        if (!probe.Reached)
        {
            return new WebsiteAuditScore
            {
                Reachable = false,
                Notes = [probe.Error ?? $"Site nao respondeu (status {probe.StatusCode?.ToString() ?? "nenhum"})."]
            };
        }

        return new WebsiteAuditScore
        {
            Reachable = true,
            Performance = Performance(probe),
            Seo = Seo(probe),
            Mobile = Mobile(probe),
            Tracking = Tracking(probe),
            Ux = Ux(profile),
            Conversion = Conversion(profile),
            Inventory = Inventory(profile),
            MultiplePortals = MultiplePortals(profile),
            ComplexIntegration = ComplexIntegration(probe, profile)
        };
    }

    // ------------------------------------------------------------------ sonda

    /// <summary>
    /// Quatro medidas, com peso. O tempo ate o primeiro byte pesa mais que o
    /// resto somado: e o unico que o visitante sente antes de qualquer pixel
    /// aparecer, e o unico que uma vitrine de veiculos lenta nao esconde.
    /// </summary>
    private static decimal? Performance(WebsiteProbeResult p)
    {
        var parts = new List<(decimal Score, decimal Weight)>();

        if (p.TimeToFirstByte is { } ttfb)
        {
            parts.Add((ttfb.TotalMilliseconds switch
            {
                <= 200 => 100m,
                <= 500 => 85m,
                <= 1000 => 65m,
                <= 2000 => 40m,
                <= 4000 => 20m,
                _ => 5m
            }, 4m));
        }

        if (p.DocumentBytes is { } bytes)
        {
            parts.Add((bytes switch
            {
                <= 100_000 => 100m,
                <= 250_000 => 85m,
                <= 500_000 => 65m,
                <= 1_000_000 => 40m,
                _ => 15m
            }, 2m));
        }

        if (p.RenderBlockingResources is { } blocking)
        {
            parts.Add((blocking switch
            {
                0 => 100m,
                <= 2 => 85m,
                <= 5 => 65m,
                <= 10 => 40m,
                _ => 15m
            }, 2m));
        }

        if (p.CompressionEnabled is { } compressed)
        {
            parts.Add((compressed ? 100m : 30m, 1m));
        }

        return Weighted(parts);
    }

    private static decimal? Seo(WebsiteProbeResult p)
    {
        var parts = new List<(decimal, decimal)>();

        void Add(bool? observed, decimal weight)
        {
            if (observed is { } v) parts.Add((v ? 100m : 0m, weight));
        }

        Add(p.HasTitle, 3m);
        Add(p.HasMetaDescription, 2m);
        Add(p.HasH1, 2m);
        Add(p.IsHttps, 3m);
        Add(p.HasCanonical, 1m);
        // Schema.org de veiculo e o que faz o estoque aparecer em rich result;
        // num setor de busca por modelo, pesa mais que canonical.
        Add(p.HasStructuredData, 2m);
        Add(p.HasSitemap, 2m);
        Add(p.HasRobotsTxt, 1m);

        return Weighted(parts);
    }

    private static decimal? Mobile(WebsiteProbeResult p)
    {
        if (p.HasViewportMeta is not { } viewport) return null;

        if (!viewport) return 10m;

        // Viewport de largura fixa e pior que nenhum: declara suporte a mobile e
        // entrega uma pagina que exige zoom.
        return p.HasFixedWidthViewport == true ? 35m : 100m;
    }

    /// <summary>
    /// Rastreio e a dimensao mais direta de todas para a AutoHous: sem
    /// analytics nem tag manager, a empresa nao sabe de onde vem lead nenhum - e
    /// nao saber e a dor que o produto resolve.
    /// </summary>
    private static decimal Tracking(WebsiteProbeResult p)
    {
        var parts = new List<(decimal, decimal)>
        {
            (p.HasAnalytics ? 100m : 0m, 4m),
            (p.HasTagManager ? 100m : 0m, 3m),
            (p.HasAdsPixel ? 100m : 0m, 2m),
            (p.HasChat ? 100m : 0m, 1m)
        };

        return Weighted(parts)!.Value;
    }

    // ----------------------------------------------------------------- agente

    /// <summary>
    /// Parte de 100 e desconta por achado. O agente nao atribui nota - ele lista
    /// problemas com evidencia, e a gravidade vira desconto aqui. Pedir a nota ao
    /// modelo devolveria a aritmetica para o lado que o ADR-0005 tirou dela.
    /// </summary>
    private static decimal? Ux(WebsiteAuditProfile? profile) =>
        FromIssues(profile, AuditArea.Ux);

    private static decimal? Conversion(WebsiteAuditProfile? profile)
    {
        if (profile?.Conversion is not { } c) return FromIssues(profile, AuditArea.Conversion);

        var parts = new List<(decimal, decimal)>();

        void Add(bool? has, decimal weight)
        {
            if (has is { } v) parts.Add((v ? 100m : 0m, weight));
        }

        Add(c.HasLeadForm, 3m);
        // WhatsApp nao e enfeite no varejo automotivo brasileiro: e o canal onde
        // a negociacao efetivamente acontece.
        Add(c.HasWhatsApp, 3m);
        Add(c.HasFinancingSimulator, 2m);
        Add(c.HasTradeIn, 1m);
        Add(c.HasScheduling, 1m);

        var baseline = Weighted(parts);
        if (baseline is null) return FromIssues(profile, AuditArea.Conversion);

        return Clamp(baseline.Value - Penalty(profile, AuditArea.Conversion));
    }

    private static decimal? Inventory(WebsiteAuditProfile? profile)
    {
        if (profile?.Inventory is not { } inv) return FromIssues(profile, AuditArea.Inventory);

        // Sem vitrine online nao ha o que pontuar em qualidade de vitrine - e o
        // zero aqui e um fato, nao uma ausencia de observacao.
        if (!inv.PublishedOnline) return 0m;

        var parts = new List<(decimal, decimal)> { (100m, 3m) };

        void Add(bool? has, decimal weight)
        {
            if (has is { } v) parts.Add((v ? 100m : 0m, weight));
        }

        Add(inv.HasSearchFilters, 2m);
        Add(inv.HasDetailPages, 2m);
        Add(inv.HasPhotos, 2m);

        return Clamp(Weighted(parts)!.Value - Penalty(profile, AuditArea.Inventory));
    }

    private static decimal? FromIssues(WebsiteAuditProfile? profile, string area)
    {
        if (profile is null) return null;

        // Nenhum achado numa area pode significar "esta tudo bem" ou "nao olhei".
        // A completude do proprio agente desempata: abaixo de 0.5 ele mesmo esta
        // dizendo que a passada foi rasa.
        var hasFindings =
            profile.Issues.Any(i => i.Area == area) || profile.Strengths.Any(s => s.Area == area);

        if (!hasFindings && profile.AuditCompleteness < 0.5m) return null;

        return Clamp(100m - Penalty(profile, area));
    }

    private static decimal Penalty(WebsiteAuditProfile? profile, string area) =>
        profile is null
            ? 0m
            : profile.Issues
                .Where(i => i.Area == area)
                .Sum(i => i.Severity.ToLowerInvariant() switch
                {
                    "high" => 25m,
                    "medium" => 12m,
                    _ => 5m
                });

    // ---------------------------------------------------- fatos de Technology Pain

    /// <summary>
    /// Um portal alem do site proprio ja e fragmentacao: o estoque passa a viver
    /// em dois lugares que ninguem sincroniza a mao sem erro.
    /// </summary>
    private static bool? MultiplePortals(WebsiteAuditProfile? profile) =>
        profile is null ? null : profile.Portals.Count > 1;

    /// <summary>
    /// Integracao complexa e o numero de CATEGORIAS distintas, e nao de sistemas.
    /// Duas ferramentas de analytics sao redundancia; um DMS, um CRM e uma
    /// plataforma de estoque de fornecedores diferentes sao tres contratos, tres
    /// suportes e nenhum dado que fecha - que e a dor de verdade.
    /// </summary>
    private static bool? ComplexIntegration(WebsiteProbeResult probe, WebsiteAuditProfile? profile)
    {
        if (profile is null && probe.Technologies.Count == 0) return null;

        var categories = probe.Technologies
            .Select(t => t.Category)
            .Concat(profile?.Integrations.Select(i => i.Category) ?? [])
            .Where(c => c is not (TechnologyCategory.Other or TechnologyCategory.Cms))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return categories >= 3;
    }

    // ------------------------------------------------------------ utilitarios

    private static decimal? Weighted(List<(decimal Score, decimal Weight)> parts)
    {
        if (parts.Count == 0) return null;

        var total = parts.Sum(p => p.Weight);
        if (total == 0) return null;

        return Math.Round(parts.Sum(p => p.Score * p.Weight) / total, 2);
    }

    private static decimal Clamp(decimal value) => Math.Round(Math.Clamp(value, 0m, 100m), 2);
}

/// <summary>
/// As sete notas na escala 0-100 de <c>website_audits</c>. Nulo significa "nao
/// observado", que e diferente de zero - ver o comentario de
/// <see cref="ScoreComponent"/> sobre a mesma distincao no Opportunity Score.
/// </summary>
public sealed record WebsiteAuditScore
{
    public required bool Reachable { get; init; }

    public decimal? Performance { get; init; }
    public decimal? Seo { get; init; }
    public decimal? Ux { get; init; }
    public decimal? Mobile { get; init; }
    public decimal? Conversion { get; init; }
    public decimal? Inventory { get; init; }
    public decimal? Tracking { get; init; }

    public bool? MultiplePortals { get; init; }
    public bool? ComplexIntegration { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Fracao das sete dimensoes efetivamente observadas.</summary>
    public decimal Coverage
    {
        get
        {
            decimal[] all = [.. new[] { Performance, Seo, Ux, Mobile, Conversion, Inventory, Tracking }
                .Where(v => v is not null)
                .Select(v => v!.Value)];

            return Math.Round(all.Length / 7m, 4);
        }
    }
}
