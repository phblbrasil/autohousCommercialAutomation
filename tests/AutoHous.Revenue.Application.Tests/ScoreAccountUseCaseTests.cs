using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

public class ScoreAccountUseCaseTests
{
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeAccountScoreRepository _scores = new();
    private readonly FakeOutboxRepository _outbox = new();
    private readonly FakeUnitOfWorkFactory _uow = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 20, 3, 0, 0, TimeSpan.Zero));
    private readonly SequentialIdGenerator _ids = new();

    private ScoreAccountUseCase Subject => new(
        _accounts, _scores, _outbox, _uow, _clock, _ids, NullLogger<ScoreAccountUseCase>.Instance);

    private void GiveFacts(Guid accountId, AccountScoringFacts? facts = null) =>
        _scores.Facts = facts ?? new AccountScoringFacts
        {
            AccountId = accountId,
            Segment = "concessionaria",
            StoreCount = 8,
            CnpjCount = 3,
            BrandCount = 2
        };

    [Fact]
    public async Task Conta_inexistente_nao_pontua()
    {
        var result = await Subject.ExecuteAsync(Guid.NewGuid());

        Assert.Equal(ScoreAccountOutcome.AccountNotFound, result.Outcome);
        Assert.Empty(_scores.Inserted);
    }

    [Fact]
    public async Task Conta_suprimida_nao_pontua()
    {
        var account = _accounts.Add(AccountStatus.Suppressed);
        GiveFacts(account.Id);

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(ScoreAccountOutcome.AccountSuppressed, result.Outcome);
        Assert.Empty(_scores.Inserted);
        Assert.Equal(0, _uow.CommitCount);
    }

    [Fact]
    public async Task Grava_score_tier_e_emite_score_ready()
    {
        var account = _accounts.Add(AccountStatus.Researched, "concessionaria");
        GiveFacts(account.Id);

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(ScoreAccountOutcome.Scored, result.Outcome);

        var (scoredAccount, score, snapshot) = Assert.Single(_scores.Inserted);
        Assert.Equal(account.Id, scoredAccount);
        Assert.True(score.Total > 0);

        // O breakdown vai para feature_snapshot: e o que responde "por que este
        // numero?" seis meses depois.
        Assert.Contains("breakdown", snapshot);

        Assert.Equal((account.Id, score.Tier), Assert.Single(_scores.Tiers));

        var evt = Assert.Single(_outbox.Enqueued);
        Assert.Equal(EventTypes.ScoreReady, evt.EventType);
        Assert.Equal(1, _uow.CommitCount);
    }

    [Fact]
    public async Task Promove_de_researched_para_scored()
    {
        var account = _accounts.Add(AccountStatus.Researched);
        GiveFacts(account.Id);

        await Subject.ExecuteAsync(account.Id);

        Assert.Equal((account.Id, AccountStatus.Researched, AccountStatus.Scored),
            Assert.Single(_accounts.Transitions));
    }

    /// <summary>
    /// Recalcular o score de uma conta ja contatada e normal — chegou um sinal
    /// novo. Empurra-la de volta para <c>scored</c> apagaria o fato de que ela ja
    /// recebeu abordagem.
    /// </summary>
    [Fact]
    public async Task Nao_regride_conta_que_ja_avancou_no_funil()
    {
        var account = _accounts.Add(AccountStatus.Contacted);
        GiveFacts(account.Id);

        var result = await Subject.ExecuteAsync(account.Id);

        Assert.Equal(ScoreAccountOutcome.Scored, result.Outcome);
        Assert.Empty(_accounts.Transitions);
        Assert.Single(_scores.Inserted);
    }

    /// <summary>
    /// O evento de entrada e baixado na MESMA transacao que grava o score. Marcar
    /// fora abriria a janela em que o evento consta processado e o score nao
    /// existe — e nada o reprocessaria.
    /// </summary>
    [Fact]
    public async Task Baixa_o_evento_de_origem_na_transacao_do_score()
    {
        var account = _accounts.Add(AccountStatus.Researched);
        GiveFacts(account.Id);

        var sourceEvent = Guid.NewGuid();
        await Subject.ExecuteAsync(account.Id, sourceEvent);

        Assert.Equal(sourceEvent, Assert.Single(_outbox.Processed));
        Assert.Equal(1, _uow.CommitCount);
    }
}
