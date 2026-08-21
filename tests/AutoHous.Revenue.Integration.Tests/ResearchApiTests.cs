using AutoHous.Revenue.Application;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// A primeira entrega tecnica da secao 36, pela borda HTTP.
/// </summary>
public class ResearchApiTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("REVENUE_DB_CONNECTION", _postgres.ConnectionString));

        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task<Guid> CreateAccountAsync(string cnpj = "11222333000181")
    {
        var response = await _client.PostAsJsonAsync("/accounts", new
        {
            cnpj,
            name = "Grupo Vento Sul",
            uf = "SP",
            municipio = "Bauru"
        });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("account_id").GetGuid();
    }

    [Fact]
    public async Task Health_confirma_conectividade_com_o_banco()
    {
        var response = await _client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Contains("reachable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cria_account_a_partir_de_cnpj()
    {
        var response = await _client.PostAsJsonAsync("/accounts", new
        {
            cnpj = "11.222.333/0001-81",
            name = "Grupo Vento Sul"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("11222333000181", body.GetProperty("cnpj").GetString());
    }

    [Fact]
    public async Task Rejeita_cnpj_invalido()
    {
        var response = await _client.PostAsJsonAsync("/accounts", new { cnpj = "11222333000182", name = "X" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cnpj_repetido_nao_cria_conta_duplicada()
    {
        var first = await CreateAccountAsync("11444777000161");
        var second = await CreateAccountAsync("11444777000161");

        Assert.Equal(first, second);

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from companies_cnpj where cnpj = '11444777000161'"));
    }

    [Fact]
    public async Task Research_responde_202_e_enfileira_o_evento()
    {
        var accountId = await CreateAccountAsync();

        var response = await _client.PostAsync($"/accounts/{accountId}/research", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/research-runs/", response.Headers.Location!.ToString()[..15]);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = body.GetProperty("research_run_id").GetGuid();

        // A conta entrou em pesquisa e o evento esta na fila - a API nao chamou
        // o Hermes de forma sincrona.
        var status = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select status::text from accounts where id = @Id", new { Id = accountId });

        Assert.Equal("researching", status);

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where event_type = 'research.requested' and status = 'pending'"));

        var runStatus = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select status from research_runs where id = @Id", new { Id = runId });

        Assert.Equal(RunStatus.Queued, runStatus);
    }

    [Fact]
    public async Task Research_em_conta_inexistente_retorna_404()
    {
        var response = await _client.PostAsync($"/accounts/{Guid.NewGuid()}/research", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Segunda_chamada_e_bloqueada_pela_transicao_de_estado()
    {
        var accountId = await CreateAccountAsync();

        Assert.Equal(HttpStatusCode.Accepted,
            (await _client.PostAsync($"/accounts/{accountId}/research", null)).StatusCode);

        // A conta ja esta em 'researching': nao ha caminho de volta para
        // 'researching' na maquina de estados, entao a segunda chamada e recusada.
        var second = await _client.PostAsync($"/accounts/{accountId}/research", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where event_type = 'research.requested'"));
    }

    [Fact]
    public async Task Conta_suprimida_nao_entra_em_pesquisa()
    {
        var accountId = await CreateAccountAsync();

        // Regra 2 da secao 25.
        var services = _postgres.BuildWorkerServices();
        var factory = services.GetRequiredService<IUnitOfWorkFactory>();
        var accounts = services.GetRequiredService<IAccountRepository>();

        await using (var uow = await factory.BeginAsync())
        {
            await accounts.TransitionAsync(uow, accountId, AccountStatus.Discovered, AccountStatus.Suppressed);
            await uow.CommitAsync();
        }

        var response = await _client.PostAsync($"/accounts/{accountId}/research", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("suprimida", await response.Content.ReadAsStringAsync());

        await services.DisposeAsync();
    }

    [Fact]
    public async Task Contexto_da_conta_alimenta_o_prompt_do_agente()
    {
        var accountId = await CreateAccountAsync();

        var context = await _client.GetFromJsonAsync<JsonElement>($"/accounts/{accountId}/context");

        Assert.Equal("Grupo Vento Sul", context.GetProperty("name").GetString());
        Assert.Equal("discovered", context.GetProperty("status").GetString());
        Assert.Equal("11222333000181", context.GetProperty("cnpjs")[0].GetString());
        Assert.Equal(0, context.GetProperty("evidenceCount").GetInt32());
    }
}
