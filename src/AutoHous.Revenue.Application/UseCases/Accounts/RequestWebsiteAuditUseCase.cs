using System.Text.Json;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public enum RequestAuditOutcome
{
    Accepted,
    AccountNotFound,
    AccountSuppressed,
    MissingDomain
}

public sealed record RequestAuditResult(
    RequestAuditOutcome Outcome,
    Guid ResearchRunId = default,
    string? Detail = null);

/// <summary>
/// Enfileira uma auditoria de site.
///
/// Espelha <see cref="RequestAccountResearchUseCase"/> nas regras que valem para
/// os dois - Regra 2 (suppression) e a idempotencia da Regra 5 -, e diverge nas
/// que nao valem:
///
/// **Sem cooldown mensal.** A Regra 3 existe porque repesquisar uma conta todo
/// mes gasta modelo sem trazer fato novo. Auditoria e outra coisa: o site muda
/// quando a empresa faz replatform, e descobrir isso rapido e o sinal de compra
/// mais direto do catalogo. Represar a auditoria por um mes esconderia
/// exatamente o evento que ela existe para pegar.
///
/// **Sem transicao de estado.** Auditar nao move a conta na maquina de estados.
/// Ela observa; quem promove e a pesquisa e o score. Por isso tambem nao ha
/// checagem de run concorrente por status - duas auditorias simultaneas
/// gravariam duas linhas em website_audits, que e append-only de proposito, e a
/// view do vigente pega a mais recente.
///
/// Nada aqui chama o Hermes: o que sai desta transacao e um evento no outbox.
/// </summary>
public sealed class RequestWebsiteAuditUseCase(
    IAccountRepository accounts,
    IResearchRunRepository researchRuns,
    IOutboxRepository outbox,
    IUnitOfWorkFactory unitOfWork,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<RequestWebsiteAuditUseCase> logger)
{
    public async Task<RequestAuditResult> ExecuteAsync(
        Guid accountId, string? url = null, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct);

        if (account is null)
        {
            return new RequestAuditResult(RequestAuditOutcome.AccountNotFound);
        }

        // Regra 2: nunca agir sobre conta suprimida.
        if (account.Status == AccountStatus.Suppressed)
        {
            return new RequestAuditResult(
                RequestAuditOutcome.AccountSuppressed,
                Detail: "Contas em suppression nao entram em auditoria nem em outbound.");
        }

        // Falhar aqui, e nao no worker: recusar na borda com um 409 legivel e
        // melhor que aceitar, enfileirar e falhar minutos depois num log que
        // ninguem esta lendo.
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(account.Domain))
        {
            return new RequestAuditResult(
                RequestAuditOutcome.MissingDomain,
                Detail: "Conta sem dominio. Rode a pesquisa antes, ou informe a url no corpo.");
        }

        var runId = ids.NewId();

        await using var uow = await unitOfWork.BeginAsync(ct);

        // run_type distingue a safra: "por que esta conta tem tres runs em
        // agosto?" so tem resposta se der para separar pesquisa de auditoria.
        await researchRuns.CreateAsync(uow, runId, accountId, "website_audit", ct);

        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = ids.NewId(),
            EventType = EventTypes.AuditRequested,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                research_run_id = runId,
                url,
                requested_by = "api"
            }),
            IdempotencyKey = IdempotencyKey.ForAudit(accountId, runId),
            Status = OutboxStatus.Pending,
            AvailableAt = clock.UtcNow
        }, ct);

        await uow.CommitAsync(ct);

        logger.LogInformation(
            "Auditoria enfileirada para a conta {AccountId}, run {ResearchRunId}", accountId, runId);

        return new RequestAuditResult(RequestAuditOutcome.Accepted, runId);
    }
}
