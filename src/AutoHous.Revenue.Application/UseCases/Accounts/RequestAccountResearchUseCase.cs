using System.Text.Json;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public enum RequestResearchOutcome
{
    Accepted,
    AccountNotFound,
    AccountSuppressed,
    ResearchInFlight,
    CooldownActive,
    InvalidTransition
}

public sealed record RequestResearchResult(
    RequestResearchOutcome Outcome,
    Guid ResearchRunId = default,
    string? Detail = null);

/// <summary>
/// Enfileira uma pesquisa. Este caso de uso concentra as regras 2, 3 e 5 da
/// governanca - suppression, cooldown e idempotencia - que antes viviam dentro
/// do lambda do endpoint HTTP.
///
/// Nada aqui chama o Hermes: a API nunca executa agente de forma sincrona. O que
/// sai desta transacao e um evento no outbox.
/// </summary>
public sealed class RequestAccountResearchUseCase(
    IAccountRepository accounts,
    IResearchRunRepository researchRuns,
    IOutboxRepository outbox,
    IUnitOfWorkFactory unitOfWork,
    IClock clock,
    IIdentifierGenerator ids,
    ILogger<RequestAccountResearchUseCase> logger)
{
    public async Task<RequestResearchResult> ExecuteAsync(
        Guid accountId, bool force = false, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct);

        if (account is null)
        {
            return new RequestResearchResult(RequestResearchOutcome.AccountNotFound);
        }

        // Regra 2: nunca agir sobre conta suprimida. Nem force fura isto.
        if (account.Status == AccountStatus.Suppressed)
        {
            return new RequestResearchResult(
                RequestResearchOutcome.AccountSuppressed,
                Detail: "Contas em suppression nao entram em pesquisa nem em outbound.");
        }

        // Ja existe um run em voo. A maquina de estados trata from == to como
        // no-op valido - correto para reprocessar um evento, mas aqui deixaria
        // passar uma SEGUNDA pesquisa da mesma conta, com o dobro de custo de IA.
        if (account.Status == AccountStatus.Researching && !force)
        {
            return new RequestResearchResult(
                RequestResearchOutcome.ResearchInFlight,
                Detail: "Ja existe um run de pesquisa em execucao para esta conta.");
        }

        // Regra 3: cooldown e regra de negocio, deliberadamente separada da
        // idempotencia. Fundir as duas impediria o retry de um run que falhou
        // dentro do mesmo mes.
        if (!force)
        {
            var recent = await researchRuns.LatestSuccessfulInMonthAsync(accountId, clock.UtcNow, ct);

            if (recent is not null)
            {
                return new RequestResearchResult(
                    RequestResearchOutcome.CooldownActive,
                    Detail: $"Run {recent.Id} concluido em {recent.FinishedAt:O}.");
            }
        }

        if (!AccountStatusTransitions.CanTransition(account.Status, AccountStatus.Researching))
        {
            return new RequestResearchResult(
                RequestResearchOutcome.InvalidTransition,
                Detail: $"Conta em '{account.Status.ToDbValue()}' nao pode entrar em pesquisa.");
        }

        var runId = ids.NewId();

        // Uma transacao: research_run + status da conta + evento no outbox.
        await using var uow = await unitOfWork.BeginAsync(ct);

        await researchRuns.CreateAsync(uow, runId, accountId, "standard", ct);
        await accounts.TransitionAsync(uow, accountId, account.Status, AccountStatus.Researching, ct);

        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = ids.NewId(),
            EventType = EventTypes.ResearchRequested,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                research_run_id = runId,
                depth = "standard",
                requested_by = "api"
            }),
            IdempotencyKey = IdempotencyKey.ForResearch(accountId, runId),
            Status = OutboxStatus.Pending,
            AvailableAt = clock.UtcNow
        }, ct);

        await uow.CommitAsync(ct);

        logger.LogInformation(
            "Pesquisa enfileirada para a conta {AccountId}, run {ResearchRunId}", accountId, runId);

        return new RequestResearchResult(RequestResearchOutcome.Accepted, runId);
    }
}
