using AutoHous.Revenue.Application;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Leitura de evidencias com a fonte junto. Este SQL vivia dentro do lambda do
/// endpoint <c>GET /accounts/{id}/evidence</c>.
/// </summary>
public sealed class EvidenceReadRepository(NpgsqlConnectionFactory connections) : IEvidenceReadRepository
{
    public async Task<IReadOnlyList<EvidenceListItem>> ListForAccountAsync(
        Guid accountId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<EvidenceListItem>(new CommandDefinition("""
            select e.id          as Id,
                   e.claim_type  as ClaimType,
                   e.claim_text  as ClaimText,
                   e.confidence  as Confidence,
                   s.url         as SourceUrl,
                   s.title       as SourceTitle,
                   s.observed_at as ObservedAt
              from evidence e
              join sources s on s.id = e.source_id
             where e.account_id = @AccountId
             order by e.created_at desc
            """, new { AccountId = accountId }, cancellationToken: ct));

        return rows.ToList();
    }
}
