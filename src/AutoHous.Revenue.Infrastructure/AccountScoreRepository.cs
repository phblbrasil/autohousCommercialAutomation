using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Fatos de scoring e historico de <c>account_scores</c>.
/// </summary>
public sealed class AccountScoreRepository(NpgsqlConnectionFactory connections) : IAccountScoreRepository
{
    /// <summary>
    /// Uma consulta reune o que hoje esta em cinco tabelas.
    ///
    /// Subconsultas escalares e nao joins: um join com <c>signals</c> e
    /// <c>account_brands</c> ao mesmo tempo multiplicaria as linhas e faria a
    /// contagem de marcas depender da quantidade de sinais.
    /// </summary>
    public async Task<AccountScoringFacts?> LoadFactsAsync(Guid accountId, CancellationToken ct = default)
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
                   (select count(*) from contacts c2
                     where c2.account_id = a.id
                       and c2.status = 'verified'::contact_status
                       and lower(coalesce(c2.seniority, '')) in
                           ('c_level', 'director', 'owner', 'head', 'vp', 'socio', 'diretor'))::int
                     as DecisionMakers,
                   (select count(*) from contact_channels ch
                      join contacts c3 on c3.id = ch.contact_id
                     where c3.account_id = a.id
                       and ch.channel = 'email'
                       and c3.status <> 'invalid'::contact_status)::int as EmailContacts,
                   (select count(*) from contact_channels ch
                      join contacts c4 on c4.id = ch.contact_id
                     where c4.account_id = a.id
                       and ch.channel in ('phone', 'whatsapp')
                       and c4.status <> 'invalid'::contact_status)::int as PhoneContacts,
                   (select count(*) from contact_channels ch
                      join contacts c5 on c5.id = ch.contact_id
                     where c5.account_id = a.id
                       and ch.channel = 'linkedin')::int as LinkedInContacts,
                   (select count(*) from contacts c6
                     where c6.account_id = a.id
                       and c6.status = 'invalid'::contact_status)::int as InvalidContacts,
                   w.performance_score    as PerformanceScore,
                   w.seo_score            as SeoScore,
                   w.multiple_portals     as MultiplePortals,
                   w.complex_integration  as ComplexIntegration
              from accounts a
              left join lateral (
                   select performance_score, seo_score, multiple_portals, complex_integration
                     from website_audits wa
                    where wa.account_id = a.id
                    order by wa.audited_at desc
                    limit 1
              ) w on true
             where a.id = @Id
            """, new { Id = accountId }, cancellationToken: ct));

        if (row is null) return null;

        var signals = await connection.QueryAsync<SignalRow>(new CommandDefinition("""
            select signal_type as SignalType, strength as Strength, observed_at as ObservedAt
              from signals
             where account_id = @Id
               and (expires_at is null or expires_at > now())
             order by observed_at desc
             limit 200
            """, new { Id = accountId }, cancellationToken: ct));

        return new AccountScoringFacts
        {
            AccountId = row.AccountId,
            Segment = row.Segment,
            StoreCount = row.StoreCount,
            InventoryEstimate = row.InventoryEstimate,
            CnpjCount = row.CnpjCount,
            BrandCount = row.BrandCount,
            HasAuthorizedBrand = row.HasAuthorizedBrand,
            Signals = [.. signals.Select(s => new ScoredSignal(
                s.SignalType, s.Strength, new DateTimeOffset(DateTime.SpecifyKind(s.ObservedAt, DateTimeKind.Utc))))],

            // Sem auditoria, a dimensao inteira fica nula e o score reporta
            // "nao observada" em vez de assumir que o site esta bom.
            // website_audits guarda numeric(5,2) na escala 0-100; o dominio
            // trabalha em 0-1. A conversao e do adaptador, nao do score.
            // A condicao inclui os dois booleanos de proposito. Uma auditoria de
            // site fora do ar nao produz nota nenhuma, mas ainda pode ter
            // observado que a empresa publica em tres portais - e descartar isso
            // por ausencia de nota jogaria fora o fato mais acionavel dela.
            Audit = row.PerformanceScore is null && row.SeoScore is null
                    && row.MultiplePortals is null && row.ComplexIntegration is null
                ? null
                : new WebsiteAuditFacts
                {
                    PerformanceScore = ToUnitScale(row.PerformanceScore),
                    SeoScore = ToUnitScale(row.SeoScore),

                    // Ate a migration 0015 estes dois nao tinham coluna: o
                    // dominio os declarava em WebsiteAuditFacts, o
                    // OpportunityScoring os lia, e o adaptador nao tinha de onde
                    // trazer. Dois dos cinco criterios de Technology Pain
                    // ficavam permanentemente "nao observados".
                    MultiplePortals = row.MultiplePortals,
                    ComplexIntegration = row.ComplexIntegration
                },

            Contacts = new ContactabilityFacts
            {
                HasDecisionMaker = row.DecisionMakers > 0,
                HasProfessionalEmail = row.EmailContacts > 0,
                HasCorporatePhone = row.PhoneContacts > 0,
                HasLinkedIn = row.LinkedInContacts > 0,
                InvalidContacts = row.InvalidContacts
            }
        };
    }

    public async Task InsertAsync(
        IUnitOfWork uow, Guid scoreId, Guid accountId, OpportunityScore score,
        string featureSnapshotJson, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into account_scores
                (id, account_id, company_fit, technology_pain, buying_signal,
                 contactability, total_score, scoring_version, feature_snapshot)
            values
                (@Id, @AccountId, @CompanyFit, @TechnologyPain, @BuyingSignal,
                 @Contactability, @TotalScore, @Version, @Snapshot::jsonb)
            """,
            new
            {
                Id = scoreId,
                AccountId = accountId,
                score.CompanyFit,
                score.TechnologyPain,
                score.BuyingSignal,
                score.Contactability,
                TotalScore = score.Total,
                Version = OpportunityScoring.Version,
                Snapshot = featureSnapshotJson
            }, uow.Tx(), cancellationToken: ct));

    public async Task UpdateAccountTierAsync(
        IUnitOfWork uow, Guid accountId, short tier, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition(
            "update accounts set tier = @Tier where id = @Id",
            new { Id = accountId, Tier = tier }, uow.Tx(), cancellationToken: ct));

    public async Task<AccountScoreView?> GetCurrentAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<AccountScoreView>(new CommandDefinition("""
            select account_id            as AccountId,
                   total_score           as TotalScore,
                   company_fit           as CompanyFit,
                   technology_pain       as TechnologyPain,
                   buying_signal         as BuyingSignal,
                   contactability        as Contactability,
                   scoring_version       as ScoringVersion,
                   feature_snapshot::text as FeatureSnapshotJson,
                   calculated_at         as CalculatedAt
              from v_account_current_score
             where account_id = @Id
            """, new { Id = accountId }, cancellationToken: ct));
    }

    private static decimal? ToUnitScale(decimal? hundredScale) =>
        hundredScale is null ? null : Math.Clamp(hundredScale.Value / 100m, 0m, 1m);

    private sealed record FactsRow
    {
        public Guid AccountId { get; init; }
        public string? Segment { get; init; }
        public int? StoreCount { get; init; }
        public int? InventoryEstimate { get; init; }
        public int CnpjCount { get; init; }
        public int BrandCount { get; init; }
        public bool HasAuthorizedBrand { get; init; }
        public int DecisionMakers { get; init; }
        public int EmailContacts { get; init; }
        public int PhoneContacts { get; init; }
        public int LinkedInContacts { get; init; }
        public int InvalidContacts { get; init; }
        public decimal? PerformanceScore { get; init; }
        public decimal? SeoScore { get; init; }
        public bool? MultiplePortals { get; init; }
        public bool? ComplexIntegration { get; init; }
    }

    /// <summary>
    /// Propriedades e nao record posicional: o Dapper casa construtor por
    /// assinatura exata e falha em tempo de execucao quando um tipo diverge.
    /// <c>ObservedAt</c> e <see cref="DateTime"/> porque e assim que o Npgsql
    /// devolve <c>timestamptz</c> — a conversao para <see cref="DateTimeOffset"/>
    /// acontece acima, com o Kind explicitado como UTC.
    /// </summary>
    private sealed record SignalRow
    {
        public string SignalType { get; init; } = string.Empty;
        public decimal Strength { get; init; }
        public DateTime ObservedAt { get; init; }
    }
}
