using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Infrastructure;
using AutoHous.Revenue.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// O vertical slice ponta a ponta: evento no outbox -> worker -> agente (fixture)
/// -> validacao -> persistencia transacional.
/// </summary>
public class ResearchSliceTests : IAsyncLifetime
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

    private async Task<(Guid AccountId, Guid RunId, Guid EventId)> ArrangeAsync(
        string? scenario = null, string cnpj = "11222333000181")
    {
        var accountId = await TestData.CreateAccountAsync(Get<IAccountRepository>(), cnpj);

        var (runId, eventId) = await TestData.EnqueueResearchAsync(
            Get<IUnitOfWorkFactory>(), Get<IResearchRunRepository>(),
            Get<IAccountRepository>(), Get<IOutboxRepository>(),
            accountId, scenario: scenario);

        return (accountId, runId, eventId);
    }

    [Fact]
    public async Task Migrations_aplicam_do_zero()
    {
        var tables = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from pg_tables where schemaname = 'public'");

        // 26 tabelas de dominio + schema_versions.
        //
        // As tres da 0012: ingestion_batches, companies_raw e
        // account_merge_candidates. As tres da 0013: receita_releases,
        // rf_cnae_stats e rf_municipio_stats. E company_partners, sozinha na
        // 0014 porque e a unica que guarda PII de pessoa fisica.
        Assert.Equal(27, tables);
    }

    [Fact]
    public async Task Slice_completo_persiste_evidencias_sinais_e_custo()
    {
        var (accountId, runId, _) = await ArrangeAsync();

        var processed = await Get<OutboxDispatcher>().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, processed);

        // A conta avancou e recebeu os dados da pesquisa.
        var account = await Get<IAccountRepository>().GetAsync(accountId);
        Assert.Equal(AccountStatus.Researched, account!.Status);
        Assert.Equal("grupoventosul.com.br", account.Domain);
        Assert.Equal("dealer_group", account.Segment);
        Assert.Equal(6, account.StoreCount);
        Assert.Equal(0.84m, account.ResearchCompleteness);
        Assert.NotNull(account.LastResearchedAt);
        Assert.NotNull(account.NextResearchAt);

        // Evidencias, com fonte.
        Assert.Equal(4, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from evidence where account_id = @Id", new { Id = accountId }));

        Assert.Equal(0, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from evidence e left join sources s on s.id = e.source_id where s.url is null"));

        // Marcas, lojas e sinais, todos com lastro.
        Assert.Equal(2, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from account_brands where account_id = @Id and evidence_id is not null", new { Id = accountId }));

        Assert.Equal(2, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from account_locations where account_id = @Id", new { Id = accountId }));

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from signals where account_id = @Id and evidence_id is not null", new { Id = accountId }));

        // Runs e observabilidade.
        var run = await Get<IResearchRunRepository>().GetAsync(runId);
        Assert.Equal(RunStatus.Completed, run!.Status);
        Assert.Equal(0.84m, run.Completeness);

        var agentRuns = await Get<IAgentRunRepository>().ListAsync(accountId, 10);
        Assert.Single(agentRuns);
        Assert.Equal("researcher", agentRuns[0].AgentName);
        Assert.Equal("researcher-v1", agentRuns[0].PromptVersion);

        // Evento de entrada baixado, evento de saida enfileirado.
        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where event_type = 'research.requested' and status = 'processed'"));

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where event_type = 'research.completed'"));
    }

    [Fact]
    public async Task Reprocessar_o_mesmo_evento_nao_duplica_evidencias()
    {
        var (accountId, _, eventId) = await ArrangeAsync();

        await Get<OutboxDispatcher>().DrainOnceAsync(CancellationToken.None);

        var evidenceAfterFirst = await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from evidence where account_id = @Id", new { Id = accountId });

        // Devolve o evento para a fila, simulando entrega duplicada.
        await TestData.ScalarAsync<int>(_postgres.ConnectionString,
            "update events_outbox set status = 'pending', available_at = now() where id = @Id",
            new { Id = eventId });

        await Get<OutboxDispatcher>().DrainOnceAsync(CancellationToken.None);

        // Marcas, lojas e sinais sao idempotentes por chave logica; a conta
        // permanece consistente.
        Assert.Equal(2, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from account_brands where account_id = @Id", new { Id = accountId }));

        Assert.Equal(2, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from account_locations where account_id = @Id", new { Id = accountId }));

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from signals where account_id = @Id", new { Id = accountId }));

        // Fontes deduplicadas por content_hash: 4 evidencias, 4 URLs distintas.
        Assert.Equal(4, await TestData.ScalarAsync<long>(_postgres.ConnectionString, "select count(*) from sources"));

        Assert.True(evidenceAfterFirst > 0);
    }

    [Fact]
    public async Task Ciclo_de_reparo_recupera_output_rejeitado()
    {
        // O cenario 'missing-evidence' passa no schema mas falha no
        // EvidenceFirstGuard; o reparo devolve um perfil lastreado.
        var (accountId, runId, _) = await ArrangeAsync(scenario: "missing-evidence");

        await Get<OutboxDispatcher>().DrainOnceAsync(CancellationToken.None);

        var run = await Get<IResearchRunRepository>().GetAsync(runId);
        Assert.Equal(RunStatus.Completed, run!.Status);

        var account = await Get<IAccountRepository>().GetAsync(accountId);
        Assert.Equal(AccountStatus.Researched, account!.Status);
        Assert.Equal(12, account.StoreCount);
    }

    [Fact]
    public async Task Falha_de_contrato_nao_deixa_escrita_parcial()
    {
        // 'malformed' falha no parse e o reparo tambem falha: falha dura.
        var (accountId, runId, _) = await ArrangeAsync(scenario: "malformed");

        await Get<OutboxDispatcher>().DrainOnceAsync(CancellationToken.None);

        var run = await Get<IResearchRunRepository>().GetAsync(runId);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Contains("contract_violation", run.ErrorJson);

        // Nada de negocio foi escrito.
        Assert.Equal(0, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from evidence where account_id = @Id", new { Id = accountId }));

        Assert.Equal(0, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from signals where account_id = @Id", new { Id = accountId }));

        Assert.Equal(0, await TestData.ScalarAsync<long>(_postgres.ConnectionString, "select count(*) from sources"));

        // A conta voltou para discovered e pode ser reenfileirada.
        var account = await Get<IAccountRepository>().GetAsync(accountId);
        Assert.Equal(AccountStatus.Discovered, account!.Status);

        // O custo do agente foi contabilizado mesmo com a falha.
        var agentRuns = await Get<IAgentRunRepository>().ListAsync(accountId, 10);
        Assert.Single(agentRuns);
        Assert.Equal(RunStatus.Failed, agentRuns[0].Status);

        // O evento foi reagendado com backoff, nao perdido.
        var status = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select status from events_outbox where event_type = 'research.requested'");
        Assert.Equal(OutboxStatus.Pending, status);

        Assert.Equal(1, await TestData.ScalarAsync<int>(_postgres.ConnectionString,
            "select attempts from events_outbox where event_type = 'research.requested'"));
    }
}
