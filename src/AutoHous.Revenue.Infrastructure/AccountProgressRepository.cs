using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Le <c>v_account_progress</c>, a view que a 0017 criou para o Orchestrator.
///
/// A logica de reuniao esta na view e nao aqui de proposito: sao dez
/// subconsultas sobre sete tabelas, e mante-las em SQL declarativo deixa o
/// planner escolher o caminho. Mais importante, a view garante o que o adaptador
/// nao garantiria sozinho - UM instante. Dez leituras separadas veriam dez
/// estados, e uma auditoria concluindo entre a terceira e a quarta faria a
/// decisao sair sobre um retrato que nunca existiu.
/// </summary>
public sealed class AccountProgressRepository(NpgsqlConnectionFactory connections) : IAccountProgressRepository
{
    public async Task<AccountProgress?> GetAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<ProgressRow>(new CommandDefinition("""
            select account_id                as AccountId,
                   status::text              as Status,
                   has_run_in_flight         as HasRunInFlight,
                   has_domain                as HasDomain,
                   research_completeness     as ResearchCompleteness,
                   last_researched_at        as LastResearchedAt,
                   next_research_at          as NextResearchAt,
                   last_audited_at           as LastAuditedAt,
                   current_score_id          as CurrentScoreId,
                   scored_at                 as ScoredAt,
                   tier                      as Tier,
                   product_fit_batch_id      as ProductFitBatchId,
                   product_fit_at            as ProductFitAt,
                   has_blocking_disqualifier as HasBlockingDisqualifier,
                   contacts_searched_at      as ContactsSearchedAt,
                   has_decision_maker        as HasDecisionMaker
              from v_account_progress
             where account_id = @Id
            """, new { Id = accountId }, cancellationToken: ct));

        if (row is null) return null;

        return new AccountProgress
        {
            AccountId = row.AccountId,
            Status = EnumExtensions.FromDbValue<AccountStatus>(row.Status),
            HasRunInFlight = row.HasRunInFlight,
            HasDomain = row.HasDomain,
            ResearchCompleteness = row.ResearchCompleteness,
            LastResearchedAt = ToOffset(row.LastResearchedAt),
            NextResearchAt = ToOffset(row.NextResearchAt),
            LastAuditedAt = ToOffset(row.LastAuditedAt),
            CurrentScoreId = row.CurrentScoreId,
            ScoredAt = ToOffset(row.ScoredAt),
            Tier = row.Tier,
            ProductFitBatchId = row.ProductFitBatchId,
            ProductFitAt = ToOffset(row.ProductFitAt),
            HasBlockingDisqualifier = row.HasBlockingDisqualifier,
            ContactsSearchedAt = ToOffset(row.ContactsSearchedAt),
            HasDecisionMaker = row.HasDecisionMaker
        };
    }

    /// <summary>
    /// O Npgsql devolve <c>timestamptz</c> como <see cref="DateTime"/>; a
    /// decisao compara datas e precisa de <see cref="DateTimeOffset"/> com o
    /// Kind explicitado. Mesma conversao que o
    /// <see cref="AccountScoreRepository"/> faz com <c>observed_at</c>.
    /// </summary>
    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private sealed record ProgressRow
    {
        public Guid AccountId { get; init; }
        public string Status { get; init; } = string.Empty;
        public bool HasRunInFlight { get; init; }
        public bool HasDomain { get; init; }
        public decimal? ResearchCompleteness { get; init; }
        public DateTime? LastResearchedAt { get; init; }
        public DateTime? NextResearchAt { get; init; }
        public DateTime? LastAuditedAt { get; init; }
        public Guid? CurrentScoreId { get; init; }
        public DateTime? ScoredAt { get; init; }
        public short? Tier { get; init; }
        public Guid? ProductFitBatchId { get; init; }
        public DateTime? ProductFitAt { get; init; }
        public bool HasBlockingDisqualifier { get; init; }
        public DateTime? ContactsSearchedAt { get; init; }
        public bool HasDecisionMaker { get; init; }
    }
}
