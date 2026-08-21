using AutoHous.Revenue.Application;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Escreve o Research Profile validado. TUDO em uma transacao: sources, evidence,
/// signals, brands, locations, a account, o research_run, o agent_run, o evento de
/// saida e a baixa do evento de entrada.
///
/// Se qualquer passo falhar, o rollback e total - nao existe estado intermediario
/// em que a conta aparece pesquisada sem as evidencias que sustentam a pesquisa.
/// </summary>
public sealed class ResearchProfilePersister(
    IUnitOfWorkFactory unitOfWork,
    IAccountRepository accounts,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IOutboxRepository outbox) : IResearchProfilePersister
{
    public async Task PersistAsync(ResearchProfilePersistRequest request, CancellationToken ct = default)
    {
        var profile = request.Profile;

        await using var uow = await unitOfWork.BeginAsync(ct);

        // 1. Fontes e evidencias. O indice em evidence[] vira id real aqui; e o
        //    mapa que permite marcas, lojas e sinais referenciarem seu lastro.
        var evidenceIds = new Guid[profile.Evidence.Count];

        for (var i = 0; i < profile.Evidence.Count; i++)
        {
            var claim = profile.Evidence[i];
            var sourceId = await UpsertSourceAsync(uow, claim, ct);

            evidenceIds[i] = await InsertEvidenceAsync(uow, request.AccountId, sourceId, claim, ct);
        }

        // 2. Marcas.
        foreach (var brand in profile.Brands)
        {
            await uow.Db().ExecuteAsync(new CommandDefinition("""
                insert into account_brands (id, account_id, brand, relationship, confidence, evidence_id)
                values (@Id, @AccountId, @Brand, @Relationship, @Confidence, @EvidenceId)
                on conflict (account_id, brand) do update
                    set relationship = excluded.relationship,
                        confidence   = excluded.confidence,
                        evidence_id  = excluded.evidence_id
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    request.AccountId,
                    Brand = brand.Name,
                    brand.Relationship,
                    brand.Confidence,
                    EvidenceId = evidenceIds[brand.EvidenceIndex]
                }, uow.Tx(), cancellationToken: ct));
        }

        // 3. Lojas. Idempotente pelo indice criado na migration 0010.
        foreach (var location in profile.Locations)
        {
            await uow.Db().ExecuteAsync(new CommandDefinition("""
                insert into account_locations
                    (id, account_id, location_type, name, address, city, state, confidence)
                values
                    (@Id, @AccountId, @LocationType, @Name, @Address, @City, @State, @Confidence)
                on conflict (account_id, coalesce(name, ''), coalesce(city, '')) do update
                    set location_type = excluded.location_type,
                        address       = excluded.address,
                        state         = excluded.state,
                        confidence    = excluded.confidence
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    request.AccountId,
                    location.LocationType,
                    location.Name,
                    location.Address,
                    location.City,
                    location.State,
                    location.Confidence
                }, uow.Tx(), cancellationToken: ct));
        }

        // 4. Sinais. Reprocessar o mesmo evento nao pode duplicar: a chave logica
        //    e (conta, tipo, momento observado).
        foreach (var signal in profile.Signals)
        {
            await uow.Db().ExecuteAsync(new CommandDefinition("""
                insert into signals
                    (id, account_id, signal_type, strength, title, description, evidence_id, observed_at)
                select @Id, @AccountId, @SignalType, @Strength, @Title, @Description, @EvidenceId, @ObservedAt
                 where not exists (
                       select 1 from signals
                        where account_id = @AccountId
                          and signal_type = @SignalType
                          and observed_at = @ObservedAt)
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    request.AccountId,
                    signal.SignalType,
                    signal.Strength,
                    signal.Title,
                    signal.Description,
                    EvidenceId = evidenceIds[signal.EvidenceIndex],
                    signal.ObservedAt
                }, uow.Tx(), cancellationToken: ct));
        }

        // 5. A account. Campos vindos da pesquisa so sobrescrevem quando o agente
        //    trouxe valor: coalesce preserva dado curado manualmente.
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update accounts
               set domain                     = coalesce(@Domain, domain),
                   segment                    = coalesce(@Segment, segment),
                   store_count                = coalesce(@StoreCount, store_count),
                   vehicle_inventory_estimate = coalesce(@InventoryEstimate, vehicle_inventory_estimate),
                   research_completeness      = @Completeness,
                   last_researched_at         = now(),
                   next_research_at           = now() + @Interval
             where id = @AccountId
            """,
            new
            {
                request.AccountId,
                profile.Domain,
                profile.Segment,
                profile.StoreCount,
                profile.InventoryEstimate,
                Completeness = profile.ResearchCompleteness,
                Interval = request.ResearchInterval
            }, uow.Tx(), cancellationToken: ct));

        await accounts.TransitionAsync(
            uow, request.AccountId, request.CurrentStatus, AccountStatus.Researched, ct);

        // 6. Runs.
        await researchRuns.CompleteAsync(
            uow, request.ResearchRunId, profile.ResearchCompleteness,
            JsonSerializer.Serialize(profile), ct);

        await agentRuns.InsertAsync(uow, request.AgentRun, ct);

        // 7. Evento de saida e baixa do evento de entrada, na mesma transacao.
        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = EventTypes.ResearchCompleted,
            AggregateType = "account",
            AggregateId = request.AccountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = request.AccountId,
                research_run_id = request.ResearchRunId,
                completeness = profile.ResearchCompleteness
            }),
            IdempotencyKey = IdempotencyKey.ForResearchCompleted(request.AccountId, request.ResearchRunId),
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        }, ct);

        await outbox.MarkProcessedAsync(uow, request.OutboxEventId, ct);

        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// Deduplica fontes. Ate armazenarmos o conteudo buscado, a URL normalizada
    /// e a identidade do documento - repesquisar a mesma pagina reusa a linha.
    /// </summary>
    private static async Task<Guid> UpsertSourceAsync(
        IUnitOfWork uow, EvidenceClaim claim, CancellationToken ct)
    {
        var url = claim.Source.Url.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant())));

        var existing = await uow.Db().ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select id from sources where content_hash = @Hash",
            new { Hash = hash }, uow.Tx(), cancellationToken: ct));

        if (existing is { } found) return found;

        var id = Guid.CreateVersion7();

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into sources (id, source_type, url, title, domain, observed_at, content_hash)
            values (@Id, @SourceType::evidence_type, @Url, @Title, @Domain, @ObservedAt, @Hash)
            -- sources_content_hash_uq e um indice PARCIAL; a inferencia de
            -- ON CONFLICT sobre indice parcial exige repetir o predicado.
            on conflict (content_hash) where content_hash is not null do nothing
            """,
            new
            {
                Id = id,
                SourceType = claim.Source.Type,
                Url = url,
                claim.Source.Title,
                Domain = SafeHost(url),
                claim.Source.ObservedAt,
                Hash = hash
            }, uow.Tx(), cancellationToken: ct));

        return await uow.Db().ExecuteScalarAsync<Guid>(new CommandDefinition(
            "select id from sources where content_hash = @Hash",
            new { Hash = hash }, uow.Tx(), cancellationToken: ct));
    }

    private static async Task<Guid> InsertEvidenceAsync(
        IUnitOfWork uow, Guid accountId, Guid sourceId, EvidenceClaim claim, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into evidence
                (id, account_id, source_id, claim_type, claim_text, extracted_value, confidence, valid_from)
            values
                (@Id, @AccountId, @SourceId, @ClaimType, @ClaimText, @ExtractedValue::jsonb, @Confidence, @ValidFrom)
            """,
            new
            {
                Id = id,
                AccountId = accountId,
                SourceId = sourceId,
                claim.ClaimType,
                claim.ClaimText,
                ExtractedValue = claim.ExtractedValue?.GetRawText(),
                claim.Confidence,
                ValidFrom = claim.Source.ObservedAt
            }, uow.Tx(), cancellationToken: ct));

        return id;
    }

    private static string? SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
}
