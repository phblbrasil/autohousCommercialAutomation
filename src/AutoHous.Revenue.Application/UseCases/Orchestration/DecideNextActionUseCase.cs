using System.Text.Json;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public sealed record DecideNextActionResult(
    NextAction Action,
    string Rationale,
    Guid? EnqueuedEventId = null,
    Guid? ResearchRunId = null);

/// <summary>
/// Orchestrator (A01), metade que age.
///
/// Consome os eventos de CONCLUSAO - pesquisa, auditoria, score, fit, contatos -
/// e emite o COMANDO seguinte. A decisao em si e
/// <see cref="AccountOrchestration.Decide"/>, funcao pura no dominio; este caso
/// de uso le o retrato, chama a decisao e escreve o efeito dela.
///
/// A separacao entre comando e conclusao e o que substitui o <c>switch</c> de
/// politica que vivia no dispatcher. O roteamento de COMANDO por tipo de evento
/// continua - "audit.requested vai para o auditor" e infraestrutura, e esta
/// certo onde esta. O que saiu de la foi a decisao de que pesquisa concluida
/// significa pontuar, e auditoria concluida significa pontuar de novo: isso e
/// politica, depende do estado inteiro da conta, e agora esta em um lugar so.
///
/// Tudo numa transacao: enfileirar o comando, criar o run quando o comando
/// precisa de um, mover o estado quando cabe, e dar baixa no evento de entrada.
/// Fora dela existiria a janela em que o evento consta processado e o proximo
/// passo nao foi pedido - e a conta pararia no meio do funil sem nada apontando
/// que parou.
/// </summary>
public sealed class DecideNextActionUseCase(
    IAccountProgressRepository progress,
    IAccountRepository accounts,
    IResearchRunRepository researchRuns,
    IOutboxRepository outbox,
    IUnitOfWorkFactory unitOfWork,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<DecideNextActionUseCase> logger)
{
    public async Task<DecideNextActionResult> ExecuteAsync(
        Guid accountId, Guid? sourceEventId = null, CancellationToken ct = default)
    {
        var snapshot = await progress.GetAsync(accountId, ct);

        if (snapshot is null)
        {
            // Conta apagada entre a conclusao e a decisao. Dar baixa e correto:
            // reagendar reprocessaria para sempre um evento cujo agregado nao
            // existe mais.
            await MarkOnlyAsync(sourceEventId, ct);

            return new DecideNextActionResult(NextAction.Stop, "conta inexistente");
        }

        var now = clock.UtcNow;
        var decision = AccountOrchestration.Decide(snapshot, now);

        logger.LogInformation(
            "Orchestrator decidiu {Action} para a conta {AccountId}: {Rationale}",
            decision.Action, accountId, decision.Rationale);

        // Acoes sem comando a emitir. Dar baixa no evento de entrada e todo o
        // efeito - e e um efeito de verdade: sem ele a fila entope de eventos
        // de conta parada.
        if (decision.Action is NextAction.Stop or NextAction.Wait or NextAction.Nurture)
        {
            await using var idle = await unitOfWork.BeginAsync(ct);

            // Nurture e o unico dos tres que mexe na conta: sair da fila quente
            // e uma transicao de estado, e nao so a ausencia de proximo passo.
            if (decision.Action == NextAction.Nurture &&
                AccountStatusTransitions.CanTransition(snapshot.Status, AccountStatus.Nurture))
            {
                await accounts.TransitionAsync(idle, accountId, snapshot.Status, AccountStatus.Nurture, ct);
            }

            if (sourceEventId is { } idleEvent)
            {
                await outbox.MarkProcessedAsync(idle, idleEvent, ct);
            }

            await idle.CommitAsync(ct);

            return new DecideNextActionResult(decision.Action, decision.Rationale);
        }

        await using var uow = await unitOfWork.BeginAsync(ct);

        Guid? runId = null;
        var eventId = ids.NewId();

        // Comandos que disparam um agente ganham research_run proprio. O
        // run_type distingue a safra: "por que esta conta tem cinco runs em
        // agosto?" so tem resposta se der para separar pesquisa de auditoria,
        // de fit e de busca de contatos.
        var runType = RunTypeFor(decision.Action);

        if (runType is not null)
        {
            runId = ids.NewId();
            await researchRuns.CreateAsync(uow, runId.Value, accountId, runType, ct);
        }

        var (eventType, idempotencyKey) = Command(decision.Action, snapshot, runId, now);

        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = eventId,
            EventType = eventType,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                research_run_id = runId,
                requested_by = "orchestrator",
                rationale = decision.Rationale
            }),
            IdempotencyKey = idempotencyKey,
            Status = OutboxStatus.Pending,
            AvailableAt = now
        }, ct);

        // MarkReady e o unico comando que tambem promove a conta: os outros
        // pedem trabalho, e quem promove e o resultado do trabalho.
        if (decision.Action == NextAction.MarkReady &&
            AccountStatusTransitions.CanTransition(snapshot.Status, AccountStatus.Ready))
        {
            await accounts.TransitionAsync(uow, accountId, snapshot.Status, AccountStatus.Ready, ct);
        }

        // Pesquisa pedida move a conta para researching, do mesmo jeito que o
        // pedido vindo da API faz. Sem isso, HasRunInFlight seria a unica
        // guarda contra pedidos empilhados, e ela depende de uma leitura.
        if (decision.Action == NextAction.Research &&
            AccountStatusTransitions.CanTransition(snapshot.Status, AccountStatus.Researching))
        {
            await accounts.TransitionAsync(uow, accountId, snapshot.Status, AccountStatus.Researching, ct);
        }

        if (sourceEventId is { } processed)
        {
            await outbox.MarkProcessedAsync(uow, processed, ct);
        }

        await uow.CommitAsync(ct);

        return new DecideNextActionResult(decision.Action, decision.Rationale, eventId, runId);
    }

    private static string? RunTypeFor(NextAction action) => action switch
    {
        NextAction.Research => "account_research",
        NextAction.Audit => "website_audit",
        NextAction.MatchProducts => "product_match",
        NextAction.FindContacts => "contact_discovery",

        // Score e aritmetica sobre fatos ja persistidos: nao chama modelo, nao
        // tem custo e nao merece uma linha em research_runs. MarkReady idem - e
        // uma transicao, nao uma execucao.
        _ => null
    };

    /// <summary>
    /// Traduz a decisao no par (tipo de evento, chave de idempotencia).
    ///
    /// As chaves nao seguem todas o mesmo formato de proposito, e a diferenca
    /// diz o que significa "de novo" para cada etapa. Fit e contatos ancoram na
    /// SAFRA que os originou - refazer sobre os mesmos fatos nao produziria
    /// nada novo. Score e pesquisa ancoram no TEMPO, porque os dois sao
    /// legitimamente repetiveis quando um fato novo chega.
    /// </summary>
    private static (string EventType, string IdempotencyKey) Command(
        NextAction action, AccountProgress snapshot, Guid? runId, DateTimeOffset now) => action switch
    {
        NextAction.Research => (
            EventTypes.ResearchRequested,
            IdempotencyKey.ForResearch(snapshot.AccountId, runId!.Value)),

        NextAction.Audit => (
            EventTypes.AuditRequested,
            IdempotencyKey.ForAudit(snapshot.AccountId, runId!.Value)),

        NextAction.Score => (
            EventTypes.ScoreRequested,
            IdempotencyKey.ForScoreRequested(snapshot.AccountId, now)),

        NextAction.MatchProducts => (
            EventTypes.MatchRequested,
            // Sem score ainda gravado o fallback e o run: e um caso que a ordem
            // das regras torna raro, e a chave precisa existir de qualquer jeito.
            IdempotencyKey.ForMatch(snapshot.AccountId, snapshot.CurrentScoreId ?? runId!.Value)),

        NextAction.FindContacts => (
            EventTypes.ContactsRequested,
            IdempotencyKey.ForContacts(snapshot.AccountId, snapshot.ProductFitBatchId ?? runId!.Value)),

        NextAction.MarkReady => (
            EventTypes.AccountReady,
            IdempotencyKey.ForAccountReady(snapshot.AccountId)),

        _ => throw new InvalidOperationException($"Acao {action} nao emite comando.")
    };

    private async Task MarkOnlyAsync(Guid? sourceEventId, CancellationToken ct)
    {
        if (sourceEventId is not { } eventId) return;

        await using var uow = await unitOfWork.BeginAsync(ct);

        await outbox.MarkProcessedAsync(uow, eventId, ct);
        await uow.CommitAsync(ct);
    }
}

/// <summary>
/// Forma minima comum a todos os eventos de conclusao que o Orchestrator
/// consome.
///
/// Um record so, e nao um por tipo de evento, porque o Orchestrator so precisa
/// de <c>account_id</c>: ele nao le o resto do payload, ele le o BANCO. E a
/// diferenca entre decidir pelo que o evento conta e decidir pelo estado da
/// conta - e a segunda e a razao de esta classe existir.
/// </summary>
public sealed record AccountEventPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("account_id")]
    public Guid AccountId { get; init; }
}
