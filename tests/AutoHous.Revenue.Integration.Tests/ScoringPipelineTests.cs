using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// A cadeia de eventos do frame 02 da V2, um elo alem da pesquisa:
/// <c>research.requested → research.completed → score.ready</c>.
///
/// Ate esta entrega, <c>research.completed</c> era marcado como processado sem
/// consumidor. Estes testes provam que ele agora produz score.
/// </summary>
public class ScoringPipelineTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _services = _postgres.BuildWorkerServices();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Roda a pesquisa e depois drena o evento que ela produziu.</summary>
    private async Task<Guid> ResearchAndScoreAsync()
    {
        var accountId = await TestData.CreateAccountAsync(Get<IAccountRepository>());

        await TestData.EnqueueResearchAsync(
            Get<IUnitOfWorkFactory>(), Get<IResearchRunRepository>(),
            Get<IAccountRepository>(), Get<IOutboxRepository>(), accountId);

        var dispatcher = Get<OutboxDispatcher>();

        await dispatcher.DrainOnceAsync(Ct);   // research.requested -> pesquisa
        await dispatcher.DrainOnceAsync(Ct);   // research.completed -> score

        return accountId;
    }

    [Fact]
    public async Task Pesquisa_concluida_produz_score_persistido()
    {
        var accountId = await ResearchAndScoreAsync();

        var score = await Get<IAccountScoreRepository>().GetCurrentAsync(accountId, Ct);

        Assert.NotNull(score);
        Assert.Equal(OpportunityScoring.Version, score.ScoringVersion);
        Assert.True(score.TotalScore > 0);
        Assert.Equal(
            score.TotalScore,
            score.CompanyFit + score.TechnologyPain + score.BuyingSignal + score.Contactability);
    }

    [Fact]
    public async Task Score_promove_a_conta_para_scored_e_grava_o_tier()
    {
        var accountId = await ResearchAndScoreAsync();

        var account = await Get<IAccountRepository>().GetAsync(accountId, Ct);

        Assert.NotNull(account);
        Assert.Equal(AccountStatus.Scored, account.Status);
        Assert.NotNull(account.Tier);
        Assert.InRange(account.Tier!.Value, 1, 4);
    }

    /// <summary>
    /// O breakdown vai para <c>feature_snapshot</c>. E ele que responde, meses
    /// depois, "por que esta conta valia 68?" — sem ele o score e um numero sem
    /// defesa.
    /// </summary>
    [Fact]
    public async Task Snapshot_guarda_o_breakdown_explicavel()
    {
        var accountId = await ResearchAndScoreAsync();

        var score = await Get<IAccountScoreRepository>().GetCurrentAsync(accountId, Ct);

        Assert.NotNull(score);
        Assert.Contains("breakdown", score.FeatureSnapshotJson);
        Assert.Contains("coverage", score.FeatureSnapshotJson);
        Assert.Contains("company_fit", score.FeatureSnapshotJson);
    }

    [Fact]
    public async Task Score_ready_e_publicado_no_outbox()
    {
        var accountId = await ResearchAndScoreAsync();

        var published = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where event_type = @Type and aggregate_id = @Id",
            new { Type = EventTypes.ScoreReady, Id = accountId });

        Assert.Equal(1, published);
    }

    [Fact]
    public async Task Evento_de_pesquisa_concluida_e_baixado()
    {
        var accountId = await ResearchAndScoreAsync();

        var pending = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where event_type = @Type and aggregate_id = @Id and status <> 'processed'",
            new { Type = EventTypes.ResearchCompleted, Id = accountId });

        Assert.Equal(0, pending);
    }

    /// <summary>
    /// Sem auditoria de site e sem contatos, a cobertura fica parcial. O score
    /// nao pode fingir certeza sobre o que ninguem observou.
    /// </summary>
    [Fact]
    public async Task Cobertura_reflete_o_que_ainda_nao_foi_observado()
    {
        var accountId = await ResearchAndScoreAsync();

        var score = await Get<IAccountScoreRepository>().GetCurrentAsync(accountId, Ct);

        Assert.NotNull(score);
        Assert.Equal(0m, score.Contactability);
        Assert.Contains("\"coverage\"", score.FeatureSnapshotJson);
    }

    [Fact]
    public async Task Conta_suprimida_nao_gera_score()
    {
        var accountId = await TestData.CreateAccountAsync(Get<IAccountRepository>());

        await using (var uow = await Get<IUnitOfWorkFactory>().BeginAsync(Ct))
        {
            await Get<IAccountRepository>().TransitionAsync(
                uow, accountId, AccountStatus.Discovered, AccountStatus.Suppressed, Ct);
            await uow.CommitAsync(Ct);
        }

        var result = await Get<ScoreAccountUseCase>().ExecuteAsync(accountId, ct: Ct);

        Assert.Equal(ScoreAccountOutcome.AccountSuppressed, result.Outcome);
        Assert.Null(await Get<IAccountScoreRepository>().GetCurrentAsync(accountId, Ct));
    }

    /// <summary>
    /// <c>account_scores</c> e append-only: recalcular acumula historico em vez
    /// de sobrescrever, e a view aponta para o vigente.
    /// </summary>
    [Fact]
    public async Task Recalculo_acumula_historico_em_vez_de_sobrescrever()
    {
        var accountId = await ResearchAndScoreAsync();

        await Get<ScoreAccountUseCase>().ExecuteAsync(accountId, ct: Ct);

        var rows = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from account_scores where account_id = @Id", new { Id = accountId });

        Assert.Equal(2, rows);

        // A view continua devolvendo exatamente um vigente.
        var current = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from v_account_current_score where account_id = @Id", new { Id = accountId });

        Assert.Equal(1, current);
    }
}
