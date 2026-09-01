using System.Security.Cryptography;
using System.Text;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain.Contracts;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Escreve <c>sources</c> e <c>evidence</c> a partir de uma lista de
/// <see cref="EvidenceClaim"/>, e devolve o mapa indice -> id que o resto da
/// persistencia referencia.
///
/// Existe porque os quatro agentes escrevem evidencia do MESMO jeito, e o
/// <see cref="WebsiteAuditPersister"/> ja carregava o comentario que explica por
/// que isso e obrigatorio e nao conveniente: os agentes citam as mesmas paginas
/// - a home da concessionaria e fonte de pesquisa, de auditoria e de argumento
/// comercial -, e a deduplicacao por <c>content_hash</c> so funciona se as
/// quatro escritas derivarem a chave da mesma forma.
///
/// Com duas copias aquilo era uma nota de cuidado. Com quatro seria uma
/// promessa que ninguem consegue manter: bastaria alguem normalizar a URL numa
/// delas para o banco passar a guardar a mesma pagina duas vezes, com dois ids,
/// e "quais contas citam esta fonte?" deixaria de ter resposta certa.
/// </summary>
internal static class EvidenceWriter
{
    /// <summary>
    /// Grava a lista inteira e devolve os ids na ORDEM ORIGINAL - o array e o
    /// mapa de <c>evidence_index</c>, e o guard ja garantiu que todo indice
    /// citado cabe dentro dele.
    /// </summary>
    internal static async Task<Guid[]> WriteAllAsync(
        IUnitOfWork uow, Guid accountId, IReadOnlyList<EvidenceClaim> claims, CancellationToken ct)
    {
        var ids = new Guid[claims.Count];

        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            var sourceId = await UpsertSourceAsync(uow, claim, ct);

            ids[i] = await InsertEvidenceAsync(uow, accountId, sourceId, claim, contactId: null, ct);
        }

        return ids;
    }

    /// <summary>
    /// Deduplica fontes pela URL normalizada.
    ///
    /// A chave e o SHA-256 da URL em minusculas, e a escolha e deliberadamente
    /// conservadora: nao remove query string, nao normaliza barra final, nao
    /// resolve redirect. Uma normalizacao mais agressiva fundiria duas paginas
    /// que sao a mesma para um humano e diferentes para o argumento - a busca de
    /// estoque com filtro e sem filtro, por exemplo.
    /// </summary>
    internal static async Task<Guid> UpsertSourceAsync(
        IUnitOfWork uow, EvidenceClaim claim, CancellationToken ct)
    {
        var url = claim.Source.Url.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant())));

        var existing = await uow.Db().ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select id from sources where content_hash = @Hash",
            new { Hash = hash }, uow.Tx(), cancellationToken: ct));

        if (existing is { } found) return found;

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into sources (id, source_type, url, title, domain, observed_at, content_hash)
            values (@Id, @SourceType::evidence_type, @Url, @Title, @Domain, @ObservedAt, @Hash)
            -- sources_content_hash_uq e um indice PARCIAL; a inferencia de
            -- ON CONFLICT sobre indice parcial exige repetir o predicado.
            on conflict (content_hash) where content_hash is not null do nothing
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                SourceType = claim.Source.Type,
                Url = url,
                claim.Source.Title,
                Domain = SafeHost(url),
                ObservedAt = Timestamps.ForPostgres(claim.Source.ObservedAt),
                Hash = hash
            }, uow.Tx(), cancellationToken: ct));

        // Releitura e nao RETURNING: com `do nothing`, o insert que perde a
        // corrida nao devolve linha, e RETURNING traria nulo em vez do id que
        // ja existe.
        return await uow.Db().ExecuteScalarAsync<Guid>(new CommandDefinition(
            "select id from sources where content_hash = @Hash",
            new { Hash = hash }, uow.Tx(), cancellationToken: ct));
    }

    /// <summary>
    /// <paramref name="contactId"/> so e preenchido pelo People Finder: uma
    /// evidencia de que "fulano e diretor comercial aqui" e afirmacao sobre a
    /// PESSOA, e a coluna existe em <c>evidence</c> desde a 0004 justamente para
    /// isso. As demais evidencias sao sobre a conta e a deixam nula.
    /// </summary>
    internal static async Task<Guid> InsertEvidenceAsync(
        IUnitOfWork uow, Guid accountId, Guid sourceId, EvidenceClaim claim,
        Guid? contactId, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into evidence
                (id, account_id, contact_id, source_id, claim_type, claim_text,
                 extracted_value, confidence, valid_from)
            values
                (@Id, @AccountId, @ContactId, @SourceId, @ClaimType, @ClaimText,
                 @ExtractedValue::jsonb, @Confidence, @ValidFrom)
            """,
            new
            {
                Id = id,
                AccountId = accountId,
                ContactId = contactId,
                SourceId = sourceId,
                claim.ClaimType,
                claim.ClaimText,
                ExtractedValue = claim.ExtractedValue?.GetRawText(),
                claim.Confidence,
                ValidFrom = Timestamps.ForPostgres(claim.Source.ObservedAt)
            }, uow.Tx(), cancellationToken: ct));

        return id;
    }

    internal static string? SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
}
