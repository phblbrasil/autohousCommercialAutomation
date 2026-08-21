using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

/// <summary>
/// As regras 2, 3 e 5 da governanca, testadas sem banco.
/// </summary>
public class RequestAccountResearchUseCaseTests
{
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeResearchRunRepository _runs = new();
    private readonly FakeOutboxRepository _outbox = new();
    private readonly FakeUnitOfWorkFactory _uow = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 20, 3, 0, 0, TimeSpan.Zero));
    private readonly SequentialIdGenerator _ids = new();

    private RequestAccountResearchUseCase Subject => new(
        _accounts, _runs, _outbox, _uow, _clock, _ids, NullLogger<RequestAccountResearchUseCase>.Instance);

    [Fact]
    public async Task Conta_inexistente_nao_enfileira()
    {
        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(RequestResearchOutcome.AccountNotFound, result.Outcome);
        Assert.Empty(_outbox.Enqueued);
    }

    [Fact]
    public async Task Conta_suprimida_e_recusada()
    {
        var account = _accounts.Add(AccountStatus.Suppressed);

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(RequestResearchOutcome.AccountSuppressed, result.Outcome);
        Assert.Empty(_outbox.Enqueued);
    }

    /// <summary>
    /// Regra 2 nao tem escape. <c>force</c> existe para furar cooldown e run em
    /// voo, nao suppression: uma conta que pediu para nao ser contatada nao volta
    /// para a fila por causa de um parametro de query.
    /// </summary>
    [Fact]
    public async Task Suppression_nao_cede_a_force()
    {
        var account = _accounts.Add(AccountStatus.Suppressed);

        var result = await Subject.ExecuteAsync(account.Id, force: true);

        Assert.Equal(RequestResearchOutcome.AccountSuppressed, result.Outcome);
        Assert.Equal(0, _uow.CommitCount);
    }

    [Fact]
    public async Task Run_em_voo_bloqueia_segunda_pesquisa()
    {
        var account = _accounts.Add(AccountStatus.Researching);

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(RequestResearchOutcome.ResearchInFlight, result.Outcome);
    }

    [Fact]
    public async Task Run_em_voo_cede_a_force()
    {
        var account = _accounts.Add(AccountStatus.Researching);

        var result = await Subject.ExecuteAsync(account.Id, force: true);

        Assert.Equal(RequestResearchOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public async Task Cooldown_recusa_repesquisa_no_mesmo_mes()
    {
        var account = _accounts.Add();

        _runs.LatestInMonth = new ResearchRun
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            RunType = "standard",
            Status = RunStatus.Completed,
            FinishedAt = _clock.UtcNow.AddDays(-3)
        };

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(RequestResearchOutcome.CooldownActive, result.Outcome);
        Assert.Empty(_outbox.Enqueued);
    }

    [Fact]
    public async Task Cooldown_cede_a_force()
    {
        var account = _accounts.Add();

        _runs.LatestInMonth = new ResearchRun
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            RunType = "standard",
            Status = RunStatus.Completed,
            FinishedAt = _clock.UtcNow.AddDays(-3)
        };

        var result = await Subject.ExecuteAsync(account.Id, force: true);

        Assert.Equal(RequestResearchOutcome.Accepted, result.Outcome);
    }

    /// <summary>
    /// Uma unica transacao cobre research_run, status e evento. Se o commit nao
    /// acontecesse, a conta ficaria em <c>researching</c> sem evento para
    /// processa-la — travada para sempre.
    /// </summary>
    [Fact]
    public async Task Enfileira_run_status_e_evento_em_uma_transacao()
    {
        var account = _accounts.Add();

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(RequestResearchOutcome.Accepted, result.Outcome);
        Assert.Single(_runs.Created);
        Assert.Equal(result.ResearchRunId, _runs.Created[0]);

        Assert.Single(_accounts.Transitions);
        Assert.Equal((account.Id, AccountStatus.Discovered, AccountStatus.Researching), _accounts.Transitions[0]);

        var evt = Assert.Single(_outbox.Enqueued);
        Assert.Equal(EventTypes.ResearchRequested, evt.EventType);
        Assert.Equal(IdempotencyKey.ForResearch(account.Id, result.ResearchRunId), evt.IdempotencyKey);

        Assert.Equal(1, _uow.CommitCount);
        Assert.All(_uow.Created, u => Assert.True(u.Disposed));
    }

    [Fact]
    public async Task Transicao_invalida_e_recusada_antes_de_qualquer_escrita()
    {
        var account = _accounts.Add(AccountStatus.Customer);

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(RequestResearchOutcome.InvalidTransition, result.Outcome);
        Assert.Empty(_runs.Created);
        Assert.Equal(0, _uow.CommitCount);
    }
}
