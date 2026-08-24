using AutoHous.Revenue.Application;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Api;
using AutoHous.Revenue.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// A primeira entrega tecnica da secao 36, pela borda HTTP.
/// </summary>
public class ResearchApiTests : IAsyncLifetime
{
    /// <summary>
    /// A API nao sobe sem credencial (ver <c>RevenueApiKeys</c>), entao o teste
    /// configura uma. 42 caracteres: acima do piso de 24 e fora da lista de
    /// placeholders.
    /// </summary>
    private const string ApiKey = "chave-de-teste-do-revenue-engine-0123456789";

    private readonly PostgresFixture _postgres = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("REVENUE_DB_CONNECTION", _postgres.ConnectionString)
            .UseSetting("REVENUE_API_KEY", ApiKey));

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
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

    // ------------------------------------------------------------ credencial

    [Fact]
    public async Task Rota_de_dados_sem_credencial_responde_401()
    {
        using var anonimo = _factory.CreateClient();

        var response = await anonimo.GetAsync("/agent-runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Escrita_sem_credencial_responde_401_antes_de_tocar_o_banco()
    {
        // A rota de escrita e a que justifica o middleware: sem ele, qualquer um
        // na rede cria conta e dispara pesquisa - que custa dinheiro de modelo.
        using var anonimo = _factory.CreateClient();

        var response = await anonimo.PostAsJsonAsync("/accounts", new
        {
            cnpj = "11222333000181",
            name = "Invasor Veiculos"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Credencial_errada_responde_401()
    {
        using var intruso = _factory.CreateClient();
        intruso.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "chave-errada-mas-do-tamanho-certo-000");

        var response = await intruso.GetAsync("/agent-runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_continua_aberto_para_o_probe()
    {
        // Liveness de orquestrador roda sem credencial.
        using var anonimo = _factory.CreateClient();

        var response = await anonimo.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Chave_antiga_e_nova_convivem_durante_a_rotacao()
    {
        const string nova = "chave-nova-da-rotacao-0123456789abcdef";
        const string antiga = "chave-antiga-da-rotacao-0123456789abcd";

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("REVENUE_DB_CONNECTION", _postgres.ConnectionString)
            .UseSetting("REVENUE_API_KEY", $"{nova},{antiga}"));

        foreach (var chave in new[] { nova, antiga })
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", chave);

            var response = await client.GetAsync("/agent-runs");

            Assert.True(
                response.IsSuccessStatusCode,
                $"A chave '{chave[..12]}...' deveria valer durante a rotacao.");
        }
    }

    [Fact]
    public void Subida_sem_credencial_falha_em_vez_de_abrir_a_api()
    {
        // Uma API que sobe sem credencial parece saudavel e responde 200 - o
        // buraco so aparece quando alguem de fora encontra.
        var configuration = new ConfigurationBuilder().Build();

        var erro = Assert.Throws<InvalidOperationException>(() => RevenueApiKeys.Load(configuration));

        Assert.Contains("REVENUE_API_KEY", erro.Message);
    }

    [Fact]
    public void Chave_curta_nao_conta_como_credencial()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["REVENUE_API_KEY"] = "curta" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => RevenueApiKeys.Load(configuration));
    }

    [Fact]
    public void Chave_de_arquivo_tem_precedencia_sobre_a_variavel()
    {
        // O formato que Docker secret e Kubernetes montam.
        var arquivo = Path.GetTempFileName();

        try
        {
            File.WriteAllText(arquivo, "chave-vinda-de-arquivo-0123456789ab\n");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["REVENUE_API_KEY"] = "chave-da-variavel-0123456789abcdef",
                    ["REVENUE_API_KEY_FILE"] = arquivo
                })
                .Build();

            var chaves = RevenueApiKeys.Load(configuration);

            Assert.True(chaves.Matches("chave-vinda-de-arquivo-0123456789ab"));
            Assert.False(chaves.Matches("chave-da-variavel-0123456789abcdef"));
        }
        finally
        {
            File.Delete(arquivo);
        }
    }

    [Fact]
    public void Arquivo_de_chave_ausente_derruba_a_subida()
    {
        // Secret nao montado em container: parar aqui e melhor que subir aberto.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REVENUE_API_KEY_FILE"] = "/run/secrets/nao-montado"
            })
            .Build();

        var erro = Assert.Throws<InvalidOperationException>(() => RevenueApiKeys.Load(configuration));

        Assert.Contains("nao-montado", erro.Message);
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
