using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Fatos de fit e a safra vigente de <c>product_fit</c>.
///
/// Le mais fundo que o <see cref="AccountScoreRepository"/> na auditoria - as
/// sete notas em vez de duas - e traz a pilha de <c>technologies</c>, que o
/// scoring geral nao usa. Nao e desperdicio: e a diferenca entre "quanta dor?" e
/// "dor de que?", e a segunda pergunta e a que escolhe o produto.
/// </summary>
public sealed class ProductFitRepository(NpgsqlConnectionFactory connections) : IProductFitRepository
{
    public async Task<ProductFitFacts?> LoadFactsAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<FactsRow>(new CommandDefinition("""
            select a.id       as AccountId,
                   a.segment  as Segment,
                   a.store_count as StoreCount,
                   a.vehicle_inventory_estimate as InventoryEstimate,
                   (select count(*) from companies_cnpj c where c.account_id = a.id)::int as CnpjCount,
                   (select count(*) from account_brands b where b.account_id = a.id)::int as BrandCount,
                   exists (
                       select 1 from account_brands b
                        where b.account_id = a.id
                          and b.relationship in ('authorized_dealer', 'concessionaria', 'dealer')
                   ) as HasAuthorizedBrand,
                   s.id as AccountScoreId,
                   w.status              as AuditStatus,
                   w.performance_score   as PerformanceScore,
                   w.seo_score           as SeoScore,
                   w.ux_score            as UxScore,
                   w.mobile_score        as MobileScore,
                   w.conversion_score    as ConversionScore,
                   w.inventory_score     as InventoryScore,
                   w.tracking_score      as TrackingScore,
                   w.multiple_portals    as MultiplePortals,
                   w.complex_integration as ComplexIntegration,
                   w.portal_count        as PortalCount
              from accounts a
              left join lateral (
                   select sc.id
                     from account_scores sc
                    where sc.account_id = a.id
                    order by sc.calculated_at desc
                    limit 1
              ) s on true
              left join lateral (
                   select wa.status, wa.performance_score, wa.seo_score, wa.ux_score,
                          wa.mobile_score, wa.conversion_score, wa.inventory_score,
                          wa.tracking_score, wa.multiple_portals, wa.complex_integration,
                          wa.portal_count
                     from website_audits wa
                    where wa.account_id = a.id
                    order by wa.audited_at desc
                    limit 1
              ) w on true
             where a.id = @Id
            """, new { Id = accountId }, cancellationToken: ct));

        if (row is null) return null;

        var technologies = await connection.QueryAsync<TechnologyRow>(new CommandDefinition("""
            select category as Category, name as Name, source as Source, confidence as Confidence
              from technologies
             where account_id = @Id
             order by category, name
             limit 200
            """, new { Id = accountId }, cancellationToken: ct));

        var signals = await connection.QueryAsync<SignalRow>(new CommandDefinition("""
            select signal_type as SignalType, strength as Strength, observed_at as ObservedAt
              from signals
             where account_id = @Id
               and strength > 0
               and (expires_at is null or expires_at > now())
             order by observed_at desc
             limit 200
            """, new { Id = accountId }, cancellationToken: ct));

        return new ProductFitFacts
        {
            AccountId = row.AccountId,
            AccountScoreId = row.AccountScoreId,
            Segment = row.Segment,
            StoreCount = row.StoreCount,
            InventoryEstimate = row.InventoryEstimate,
            CnpjCount = row.CnpjCount,
            BrandCount = row.BrandCount,
            HasAuthorizedBrand = row.HasAuthorizedBrand,
            Audit = ToAuditDetail(row),
            Technologies = [.. technologies.Select(t =>
                new AccountTechnology(t.Category, t.Name, t.Source, t.Confidence))],
            Signals = [.. signals.Select(s => new ScoredSignal(
                s.SignalType, s.Strength, new DateTimeOffset(DateTime.SpecifyKind(s.ObservedAt, DateTimeKind.Utc))))]
        };
    }

    /// <summary>
    /// Sem auditoria nenhuma, <c>null</c>: o fit reporta cada criterio de site
    /// como nao observado em vez de assumir que esta bom.
    ///
    /// Com auditoria de site FORA DO AR, um objeto com <c>Reachable=false</c> e
    /// notas nulas. A distincao importa: dominio morto e o argumento mais forte
    /// que existe para FrontCar, e trata-lo como ausencia de dado mandaria a
    /// conta com o pior site do funil para o fim da fila.
    /// </summary>
    private static WebsiteAuditDetail? ToAuditDetail(FactsRow row)
    {
        if (row.AuditStatus is null) return null;

        return new WebsiteAuditDetail
        {
            Reachable = !string.Equals(row.AuditStatus, "unreachable", StringComparison.Ordinal),
            Performance = ToUnitScale(row.PerformanceScore),
            Seo = ToUnitScale(row.SeoScore),
            Ux = ToUnitScale(row.UxScore),
            Mobile = ToUnitScale(row.MobileScore),
            Conversion = ToUnitScale(row.ConversionScore),
            Inventory = ToUnitScale(row.InventoryScore),
            Tracking = ToUnitScale(row.TrackingScore),
            MultiplePortals = row.MultiplePortals,
            ComplexIntegration = row.ComplexIntegration,
            PortalCount = row.PortalCount
        };
    }

    public async Task<IReadOnlyList<ProductFitView>> GetCurrentAsync(
        Guid accountId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<ProductFitView>(new CommandDefinition("""
            select account_id                    as AccountId,
                   product                       as Product,
                   score                         as Score,
                   recommended_entry             as RecommendedEntry,
                   reasons::text                 as ReasonsJson,
                   objections::text              as ObjectionsJson,
                   recommended_personas::text    as RecommendedPersonasJson,
                   calculated_at                 as CalculatedAt
              from v_account_current_fit
             where account_id = @Id
             order by score desc
            """, new { Id = accountId }, cancellationToken: ct));

        return [.. rows];
    }

    /// <summary>
    /// <c>product_fit.score</c> e <c>website_audits.*_score</c> guardam a escala
    /// 0-100; o dominio trabalha em 0-1. A conversao e do adaptador, como no
    /// <see cref="AccountScoreRepository"/>.
    /// </summary>
    private static decimal? ToUnitScale(decimal? hundredScale) =>
        hundredScale is null ? null : Math.Clamp(hundredScale.Value / 100m, 0m, 1m);

    private sealed record FactsRow
    {
        public Guid AccountId { get; init; }
        public Guid? AccountScoreId { get; init; }
        public string? Segment { get; init; }
        public int? StoreCount { get; init; }
        public int? InventoryEstimate { get; init; }
        public int CnpjCount { get; init; }
        public int BrandCount { get; init; }
        public bool HasAuthorizedBrand { get; init; }
        public string? AuditStatus { get; init; }
        public decimal? PerformanceScore { get; init; }
        public decimal? SeoScore { get; init; }
        public decimal? UxScore { get; init; }
        public decimal? MobileScore { get; init; }
        public decimal? ConversionScore { get; init; }
        public decimal? InventoryScore { get; init; }
        public decimal? TrackingScore { get; init; }
        public bool? MultiplePortals { get; init; }
        public bool? ComplexIntegration { get; init; }
        public int? PortalCount { get; init; }
    }

    private sealed record TechnologyRow
    {
        public string Category { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public decimal Confidence { get; init; }
    }

    private sealed record SignalRow
    {
        public string SignalType { get; init; } = string.Empty;
        public decimal Strength { get; init; }
        public DateTime ObservedAt { get; init; }
    }
}
