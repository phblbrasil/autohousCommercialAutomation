using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// A cadeia de eventos do frame 02 da V2, da pesquisa ate o score.
///
/// Com o Orchestrator (A01), a cadeia deixou de ter comprimento fixo:
/// <c>research.completed</c> vai para quem decide, e uma conta COM dominio passa
/// pela auditoria antes de pontuar. Por isso estes testes drenam ate a
/// CONDICAO - "existe score?" - em vez de contar saltos.
///
/// Contar saltos fixaria a forma da cadeia, que e exatamente o que o
/// Orchestrator existe para poder mudar sem reescrever teste.
/// </summary>
public class ScoringPipelineTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();

        // A sonda de site e substituida por uma que nao sai para a rede. Estes
        // testes sao sobre SCORING; a auditoria entra na cadeia porque o
        // Orchestrator a coloca la, e deixar o HTTP real no caminho tornaria o
        // resultado dependente de o dominio da fixture existir hoje.
        _services = _postgres.BuildWorkerServices(services =>
            services.AddSingleton<IWebsiteProbe>(new UnreachableProbe()));
    }

    /// <summary>
    /// Site inalcancavel: e o resultado que a sonda real produziria para o
    /// dominio da fixture, e o caminho de codigo que ele exercita - auditoria
    /// gravada sem passar pelo agente - e o mesmo.
    /// </summary>
    private sealed class UnreachableProbe : IWebsiteProbe
    {
        public string Name => "unreachable-probe";

        public Task<WebsiteProbeResult> ProbeAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(WebsiteProbeResult.Unreachable(url, "sonda desligada no teste", DateTimeOffset.UtcNow));
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
        var scores = Get<IAccountScoreRepository>();

        await TestData.DrainUntilAsync(
            dispatcher,
            async () => await scores.GetCurrentAsync(accountId, Ct) is not null,
            Ct,
            _postgres.ConnectionString);

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
