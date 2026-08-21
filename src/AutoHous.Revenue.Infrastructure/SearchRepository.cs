using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Busca full-text (portugues, sem acento) e casamento difuso por trigrama.
///
/// A configuracao 'portuguese_unaccent' e o indice GIN vem da migration 0011.
/// A consulta usa websearch_to_tsquery, que aceita a sintaxe que o usuario ja
/// conhece de buscadores: aspas para frase, OR para alternativa, "-" para excluir.
/// </summary>
public sealed class SearchRepository(NpgsqlConnectionFactory connections) : ISearchRepository
{
    private const string Config = "portuguese_unaccent";

    public async Task<IReadOnlyList<AccountSearchHit>> SearchAccountsAsync(
        string query, int limit, CancellationToken ct = default)
    {
        var expanded = SearchQueryExpander.Expand(query);
        if (string.IsNullOrEmpty(expanded)) return [];

        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<AccountSearchHit>(new CommandDefinition($"""
            select a.id            as Id,
                   a.name          as Name,
                   a.domain        as Domain,
                   a.segment       as Segment,
                   a.city          as City,
                   a.status::text  as Status,
                   ts_rank_cd(a.search_vector, q) as Rank
              from accounts a, websearch_to_tsquery('{Config}', @Query) q
             where a.search_vector @@ q
             order by Rank desc, a.name
             limit @Limit
            """, new { Query = expanded, Limit = limit }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// O caso de uso que mais paga: "quais contas tem evidencia de expansao?"
    /// deixa de exigir leitura manual de cada research profile.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceSearchHit>> SearchEvidenceAsync(
        string query, int limit, CancellationToken ct = default)
    {
        var expanded = SearchQueryExpander.Expand(query);
        if (string.IsNullOrEmpty(expanded)) return [];

        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<EvidenceSearchHit>(new CommandDefinition($"""
            select e.id          as Id,
                   e.account_id  as AccountId,
                   a.name        as AccountName,
                   e.claim_type  as ClaimType,
                   e.claim_text  as ClaimText,
                   ts_headline('{Config}', e.claim_text, q,
                               'StartSel=[, StopSel=], MaxWords=30, MinWords=10') as Headline,
                   e.confidence  as Confidence,
                   s.url         as SourceUrl,
                   ts_rank_cd(e.search_vector, q) as Rank
              from evidence e
              join accounts a on a.id = e.account_id
              left join sources s on s.id = e.source_id,
                   websearch_to_tsquery('{Config}', @Query) q
             where e.search_vector @@ q
             order by Rank desc, e.confidence desc nulls last
             limit @Limit
            """, new { Query = expanded, Limit = limit }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Casamento difuso por trigrama - base das features de similaridade do §11.
    ///
    /// Deliberadamente NAO usa full-text: o stemmer e as stopwords existem para
    /// prosa. Para razao social, similaridade de trigrama e a ferramenta correta.
    ///
    /// As faixas devolvidas seguem o §11, mas a decisao de merge continua sendo da
    /// plataforma - isto aqui so ordena candidatos.
    /// </summary>
    public async Task<IReadOnlyList<SimilarAccountHit>> FindSimilarAccountsAsync(
        Guid accountId, decimal threshold, int limit, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<SimilarAccountHit>(new CommandDefinition("""
            with alvo as (select normalized_name from accounts where id = @AccountId)
            select a.id              as Id,
                   a.name            as Name,
                   a.normalized_name as NormalizedName,
                   a.city            as City,
                   a.state           as State,
                   round(similarity(a.normalized_name, alvo.normalized_name)::numeric, 4) as Similarity,
                   case
                     when similarity(a.normalized_name, alvo.normalized_name) >= 0.90 then 'auto'
                     when similarity(a.normalized_name, alvo.normalized_name) >= 0.75 then 'provavel'
                     else 'revisao'
                   end as Recommendation
              from accounts a, alvo
             where a.id <> @AccountId
               and a.normalized_name is not null
               and alvo.normalized_name is not null
               and similarity(a.normalized_name, alvo.normalized_name) >= @Threshold
             order by Similarity desc
             limit @Limit
            """, new { AccountId = accountId, Threshold = threshold, Limit = limit }, cancellationToken: ct));

        return rows.ToList();
    }
}
