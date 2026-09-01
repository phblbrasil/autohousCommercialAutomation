using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Escreve os contatos descobertos. TUDO em uma transacao: sources, evidence, as
/// linhas de <c>contacts</c> e <c>contact_channels</c>, a ligacao
/// contato-evidencia, o research_run, o agent_run, o evento de saida e a baixa
/// do evento de entrada.
///
/// Duas coisas acontecem aqui que nao acontecem nos outros persisters, e as duas
/// existem porque o que se grava e PII de pessoa fisica:
///
/// 1. **A classificacao de persona e nossa.** O cargo vem como a fonte o
///    escreveu; <see cref="PersonaCatalog"/> o traduz para a taxonomia do
///    catalogo, e e a traducao que vai para <c>contacts.persona</c>. A sugestao
///    do agente fica em <c>agent_persona</c>, ao lado - a divergencia entre as
///    duas leituras e o sinal mais barato de que a taxonomia precisa de regra
///    nova.
/// 2. **A politica de canal e aplicada na escrita.** E-mail em provedor pessoal
///    entra marcado; canal que nao normaliza entra sem chave de dedupe; canal
///    abaixo do piso nao chega ate aqui porque o guard ja recusou o run.
///
/// A escrita e idempotente pelos indices <c>contacts_identity_uq</c> (0003) e
/// <c>unique(contact_id, channel, normalized_value)</c>. Reexecutar a busca
/// atualiza o que mudou em vez de duplicar a agenda - que e o defeito que a
/// propria 0003 registrou como "sem esta restricao o People Finder acumula
/// duplicatas silenciosamente a cada execucao".
/// </summary>
public sealed class ContactPersister(
    IUnitOfWorkFactory unitOfWork,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IOutboxRepository outbox) : IContactPersister
{
    private static readonly JsonSerializerOptions ContactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<ContactPersistResult> PersistAsync(
        ContactPersistRequest request, CancellationToken ct = default)
    {
        var profile = request.Profile;
        var rejected = new List<string>();

        var contactsWritten = 0;
        var channelsWritten = 0;
        var hasDecisionMaker = false;

        await using var uow = await unitOfWork.BeginAsync(ct);

        // 1. Fontes e evidencias, na ordem do contrato: o array e o mapa de
        //    evidence_index, e o guard ja garantiu que todo indice cabe nele.
        var evidenceIds = await EvidenceWriter.WriteAllAsync(uow, request.AccountId, profile.Evidence, ct);

        foreach (var claim in profile.Contacts)
        {
            var persona = PersonaCatalog.Classify(claim.JobTitle);
            var normalizedName = NameNormalizer.Normalize(claim.FullName);

            if (normalizedName.Length == 0)
            {
                // Sem nome normalizavel nao ha identidade, e sem identidade o
                // indice unico nao protege nada: a proxima execucao gravaria a
                // mesma pessoa de novo.
                rejected.Add($"contato '{claim.FullName}': nome nao normalizavel");
                continue;
            }

            // A URL da evidencia de identidade vira `contacts.source_url`: e o
            // atalho que quem abre a ficha usa para conferir a pessoa sem
            // percorrer a tabela de ligacao.
            var identitySource = At(profile.Evidence, claim.EvidenceIndex)?.Source.Url;

            var contactId = await UpsertContactAsync(
                uow, request, claim, persona, normalizedName, identitySource, ct);

            contactsWritten++;

            if (PersonaCatalog.IsDecisionMaker(persona?.Seniority))
            {
                hasDecisionMaker = true;
            }

            // Evidencia da IDENTIDADE: "esta pessoa ocupa este cargo aqui".
            await LinkEvidenceAsync(uow, contactId, At(evidenceIds, claim.EvidenceIndex), "identity", ct);

            foreach (var channel in claim.Channels)
            {
                var normalized = ContactChannelNormalizer.Normalize(channel.Channel, channel.Value);

                if (normalized is null)
                {
                    // Entra assim mesmo. `normalized_value` nulo escapa do indice
                    // unico e perde a dedupe, o que e melhor que descartar um
                    // contato porque o formato surpreendeu o normalizador - um
                    // telefone com ramal ainda e um telefone.
                    rejected.Add($"canal '{channel.Channel}' de '{claim.FullName}': valor nao normalizavel, gravado sem dedupe");
                }

                var channelEvidenceId = At(evidenceIds, channel.EvidenceIndex);

                await InsertChannelAsync(
                    uow, contactId, channel, normalized, channelEvidenceId, request.AccountDomain, ct);

                // Evidencia do CANAL, com escopo proprio. E a razao de
                // `claim_scope` existir na 0017: sem ele, "onde vimos este
                // e-mail?" e "onde vimos que esta pessoa trabalha aqui?" seriam
                // a mesma linha - e o guard que separa as duas descobertas
                // perderia o sentido no momento da escrita.
                await LinkEvidenceAsync(uow, contactId, channelEvidenceId, channel.Channel, ct);

                channelsWritten++;
            }
        }

        // 2. Runs. O run_type `contact_discovery` e o que a view
        //    v_account_progress le como "ja procuramos" - e por isso ele
        //    completa mesmo quando a busca nao achou ninguem. Sem essa linha, o
        //    Orchestrator pediria a mesma busca a cada evento.
        await researchRuns.CompleteAsync(
            uow, request.ResearchRunId,
            profile.SearchCompleteness,
            JsonSerializer.Serialize(new
            {
                contacts = contactsWritten,
                channels = channelsWritten,
                has_decision_maker = hasDecisionMaker,
                searched_without_result = profile.SearchedWithoutResult,
                rejected_by_policy = rejected
            }, ContactJson),
            ct);

        await agentRuns.InsertAsync(uow, request.AgentRun, ct);

        // 3. Evento de saida e baixa do de entrada, na mesma transacao.
        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = EventTypes.ContactsFound,
            AggregateType = "account",
            AggregateId = request.AccountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = request.AccountId,
                research_run_id = request.ResearchRunId,
                contacts = contactsWritten,
                channels = channelsWritten,
                has_decision_maker = hasDecisionMaker
            }),
            IdempotencyKey = IdempotencyKey.ForContactsFound(request.AccountId, request.ResearchRunId),
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        }, ct);

        await outbox.MarkProcessedAsync(uow, request.OutboxEventId, ct);

        await uow.CommitAsync(ct);

        return new ContactPersistResult
        {
            ContactsPersisted = contactsWritten,
            ChannelsPersisted = channelsWritten,
            HasDecisionMaker = hasDecisionMaker,
            RejectedByPolicy = rejected
        };
    }

    /// <summary>
    /// Idempotente pelo <c>contacts_identity_uq</c> da 0003. O conflito ATUALIZA
    /// em vez de ignorar: uma segunda busca costuma trazer confianca maior ou um
    /// cargo mais preciso, e descartar isso deixaria a agenda congelada na
    /// primeira execucao.
    ///
    /// O status nao entra no update. Um contato marcado <c>invalid</c> por
    /// bounce, ou <c>suppressed</c> por pedido da pessoa, nao pode voltar a
    /// <c>discovered</c> porque o agente o encontrou de novo - a Regra 2 vale
    /// para pessoa do mesmo jeito que vale para conta.
    /// </summary>
    private static async Task<Guid> UpsertContactAsync(
        IUnitOfWork uow, ContactPersistRequest request, ContactClaim claim,
        PersonaMatch? persona, string normalizedName, string? sourceUrl, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();

        return await uow.Db().ExecuteScalarAsync<Guid>(new CommandDefinition("""
            insert into contacts
                (id, account_id, full_name, normalized_name, job_title, department,
                 seniority, persona, agent_persona, confidence, source_url,
                 research_run_id, agent_run_id, last_verified_at)
            values
                (@Id, @AccountId, @FullName, @NormalizedName, @JobTitle, @Department,
                 @Seniority, @Persona, @AgentPersona, @Confidence, @SourceUrl,
                 @ResearchRunId, @AgentRunId, now())
            on conflict (account_id, normalized_name, coalesce(job_title, ''))
              where normalized_name is not null
            do update set
                department       = coalesce(excluded.department, contacts.department),
                seniority        = coalesce(excluded.seniority, contacts.seniority),
                persona          = coalesce(excluded.persona, contacts.persona),
                agent_persona    = coalesce(excluded.agent_persona, contacts.agent_persona),
                confidence       = greatest(excluded.confidence, contacts.confidence),
                source_url       = coalesce(excluded.source_url, contacts.source_url),
                research_run_id  = excluded.research_run_id,
                agent_run_id     = excluded.agent_run_id,
                last_verified_at = now(),
                updated_at       = now()
            returning id
            """,
            new
            {
                Id = id,
                request.AccountId,
                FullName = claim.FullName.Trim(),
                NormalizedName = normalizedName,
                JobTitle = claim.JobTitle?.Trim(),
                Department = claim.Department ?? persona?.Department,

                // Persona vazia significa "cargo reconhecido so na senioridade":
                // sabemos que e um diretor de alguma coisa, e nao de que.
                Seniority = persona?.Seniority,
                Persona = string.IsNullOrEmpty(persona?.Persona) ? null : persona.Persona,
                AgentPersona = claim.Persona,

                claim.Confidence,
                SourceUrl = sourceUrl,
                request.ResearchRunId,
                AgentRunId = request.AgentRun.Id
            }, uow.Tx(), cancellationToken: ct));
    }

    private static async Task InsertChannelAsync(
        IUnitOfWork uow, Guid contactId, ChannelClaim channel, string? normalized,
        Guid? evidenceId, string? accountDomain, CancellationToken ct) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into contact_channels
                (id, contact_id, channel, value, normalized_value, confidence,
                 is_professional, matches_account_domain, evidence_id)
            values
                (@Id, @ContactId, @Channel, @Value, @Normalized, @Confidence,
                 @IsProfessional, @MatchesDomain, @EvidenceId)
            on conflict (contact_id, channel, normalized_value) do update
                set confidence             = greatest(excluded.confidence, contact_channels.confidence),
                    is_professional        = excluded.is_professional,
                    matches_account_domain = excluded.matches_account_domain,
                    evidence_id            = coalesce(excluded.evidence_id, contact_channels.evidence_id)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                ContactId = contactId,
                channel.Channel,
                channel.Value,
                Normalized = normalized,
                channel.Confidence,

                // A distincao profissional/pessoal so faz sentido para e-mail.
                // Telefone e LinkedIn publicados como canal da empresa sao
                // profissionais por construcao - o agente nao pode registrar
                // telefone pessoal nao publicado.
                IsProfessional = channel.Channel == ContactChannel.Email
                    ? ContactPolicy.IsProfessionalEmail(normalized ?? channel.Value)
                    : true,

                MatchesDomain = channel.Channel == ContactChannel.Email &&
                    ContactPolicy.MatchesAccountDomain(normalized ?? channel.Value, accountDomain),

                EvidenceId = evidenceId
            }, uow.Tx(), cancellationToken: ct));

    private static async Task LinkEvidenceAsync(
        IUnitOfWork uow, Guid contactId, Guid? evidenceId, string scope, CancellationToken ct)
    {
        if (evidenceId is not { } id) return;

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into contact_evidence (contact_id, evidence_id, claim_scope)
            values (@ContactId, @EvidenceId, @Scope)
            on conflict do nothing
            """,
            new { ContactId = contactId, EvidenceId = id, Scope = scope },
            uow.Tx(), cancellationToken: ct));
    }

    private static Guid? At(Guid[] evidenceIds, int index) =>
        index >= 0 && index < evidenceIds.Length ? evidenceIds[index] : null;

    private static EvidenceClaim? At(IReadOnlyList<EvidenceClaim> claims, int index) =>
        index >= 0 && index < claims.Count ? claims[index] : null;
}
