using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Escreve a auditoria validada. TUDO em uma transacao: sources, evidence, a
/// linha de <c>website_audits</c>, a ligacao evidencia-auditoria,
/// <c>technologies</c>, o research_run, o agent_run, o evento de saida e a baixa
/// do evento de entrada.
///
/// Mesma garantia do <see cref="ResearchProfilePersister"/>, pela mesma razao:
/// nao pode existir um estado em que a conta tem nota de auditoria sem as
/// evidencias que a sustentam.
/// </summary>
public sealed class WebsiteAuditPersister(
    IUnitOfWorkFactory unitOfWork,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IOutboxRepository outbox) : IWebsiteAuditPersister
{
    private static readonly JsonSerializerOptions ProbeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task PersistAsync(WebsiteAuditPersistRequest request, CancellationToken ct = default)
    {
        var profile = request.Profile;
        var score = request.Score;
        var auditId = Guid.CreateVersion7();

        await using var uow = await unitOfWork.BeginAsync(ct);

        // 1. Fontes e evidencias. Mesmo caminho do Researcher: o indice em
        //    evidence[] vira id real, e e o mapa que o resto referencia.
        var evidenceIds = profile is null
            ? []
            : await EvidenceWriter.WriteAllAsync(uow, request.AccountId, profile.Evidence, ct);

        // 2. A auditoria. `probe` guarda a medicao CRUA: as sete notas sao
        //    derivadas dela, e guardar so o derivado impediria recalcular uma
        //    safra antiga quando a formula mudar - o mesmo motivo que faz
        //    account_scores guardar feature_snapshot.
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into website_audits
                (id, account_id, url, final_url, status,
                 performance_score, seo_score, ux_score, mobile_score,
                 conversion_score, inventory_score, tracking_score,
                 multiple_portals, complex_integration, portal_count,
                 issues, strengths, probe, research_run_id, agent_run_id, audited_at)
            values
                (@Id, @AccountId, @Url, @FinalUrl, @Status,
                 @Performance, @Seo, @Ux, @Mobile,
                 @Conversion, @Inventory, @Tracking,
                 @MultiplePortals, @ComplexIntegration, @PortalCount,
                 @Issues::jsonb, @Strengths::jsonb, @Probe::jsonb,
                 @ResearchRunId, @AgentRunId, @AuditedAt)
            """,
            new
            {
                Id = auditId,
                request.AccountId,
                Url = request.Probe.RequestedUrl,
                FinalUrl = request.Probe.FinalUrl,
                Status = score.Reachable ? "completed" : "unreachable",
                score.Performance,
                score.Seo,
                score.Ux,
                score.Mobile,
                score.Conversion,
                score.Inventory,
                score.Tracking,
                score.MultiplePortals,
                score.ComplexIntegration,

                // Nulo quando o agente nao rodou - site fora do ar nao observou
                // canal externo nenhum, e gravar 0 afirmaria que nao ha.
                PortalCount = profile?.Portals.Count,
                Issues = JsonSerializer.Serialize(profile?.Issues ?? [], ProbeJson),
                Strengths = JsonSerializer.Serialize(profile?.Strengths ?? [], ProbeJson),
                Probe = JsonSerializer.Serialize(request.Probe, ProbeJson),
                request.ResearchRunId,
                AgentRunId = request.AgentRun.Id,
                AuditedAt = Timestamps.ForPostgres(request.Probe.ObservedAt)
            }, uow.Tx(), cancellationToken: ct));

        // 3. Ligacao evidencia-auditoria. A tabela que a 0015 criou no lugar do
        //    array `evidence_ids`, que nao tinha integridade referencial.
        foreach (var evidenceId in evidenceIds)
        {
            await uow.Db().ExecuteAsync(new CommandDefinition("""
                insert into website_audit_evidence (website_audit_id, evidence_id)
                values (@AuditId, @EvidenceId)
                on conflict do nothing
                """,
                new { AuditId = auditId, EvidenceId = evidenceId }, uow.Tx(), cancellationToken: ct));
        }

        // 4. Tecnologias medidas pela sonda. source='probe': a propria medicao e
        //    a fonte, e por isso nao levam evidence_id - o check da 0015 so o
        //    exige de source='agent'.
        foreach (var tech in request.Probe.Technologies)
        {
            await UpsertTechnologyAsync(
                uow, request.AccountId, auditId, tech.Category, tech.Name,
                tech.Confidence, TechnologySource.Probe, evidenceId: null, ct);
        }

        // 5. Integracoes INFERIDAS pelo agente. Cada uma carrega evidencia; sem
        //    ela o banco recusa a linha, e e assim que a Regra 1 deixa de
        //    depender de alguem lembrar dela.
        if (profile is not null)
        {
            foreach (var integration in profile.Integrations)
            {
                await UpsertTechnologyAsync(
                    uow, request.AccountId, auditId, integration.Category, integration.System,
                    integration.Confidence, TechnologySource.Agent,
                    evidenceIds[integration.EvidenceIndex], ct);
            }
        }

        // 6. Runs. A auditoria NAO transiciona a conta: ela observa, e quem
        //    promove no funil e a pesquisa e o score.
        await researchRuns.CompleteAsync(
            uow, request.ResearchRunId,
            profile?.AuditCompleteness ?? 0m,
            JsonSerializer.Serialize(new { probe = request.Probe, score, profile }, ProbeJson),
            ct);

        await agentRuns.InsertAsync(uow, request.AgentRun, ct);

        // 7. Evento de saida e baixa do de entrada, na mesma transacao.
        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = EventTypes.AuditCompleted,
            AggregateType = "account",
            AggregateId = request.AccountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = request.AccountId,
                research_run_id = request.ResearchRunId,
                website_audit_id = auditId,
                reachable = score.Reachable,
                coverage = score.Coverage
            }),
            IdempotencyKey = IdempotencyKey.ForAuditCompleted(request.AccountId, request.ResearchRunId),
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        }, ct);

        await outbox.MarkProcessedAsync(uow, request.OutboxEventId, ct);

        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// Idempotente pelo indice <c>technologies_identity_uq</c>. Reauditar a mesma
    /// conta atualiza a linha e move <c>last_detected_at</c> em vez de duplicar a
    /// pilha inteira - e a janela entre <c>first_detected_at</c> e
    /// <c>last_detected_at</c> e o que permite responder "desde quando eles usam
    /// isto?", que e sinal de replatform.
    /// </summary>
    private static async Task UpsertTechnologyAsync(
        IUnitOfWork uow, Guid accountId, Guid auditId, string category, string name,
        decimal confidence, string source, Guid? evidenceId, CancellationToken ct) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into technologies
                (id, account_id, category, name, confidence, source, evidence_id, website_audit_id)
            values
                (@Id, @AccountId, @Category, @Name, @Confidence, @Source, @EvidenceId, @AuditId)
            on conflict (account_id, category, lower(name)) do update
                set confidence       = excluded.confidence,
                    source           = excluded.source,
                    evidence_id      = coalesce(excluded.evidence_id, technologies.evidence_id),
                    website_audit_id = excluded.website_audit_id,
                    last_detected_at = now(),
                    updated_at       = now()
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                Category = category,
                Name = name,
                Confidence = confidence,
                Source = source,
                EvidenceId = evidenceId,
                AuditId = auditId
            }, uow.Tx(), cancellationToken: ct));
}
