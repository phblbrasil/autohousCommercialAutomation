using AutoHous.Revenue.Application;
using AutoHous.Revenue.Infrastructure;
using AutoHous.Revenue.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// Busca full-text em portugues e casamento difuso por trigrama (migration 0011),
/// exercitados sobre dados reais produzidos pelo slice de pesquisa.
/// </summary>
public class FullTextSearchTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private ServiceProvider _services = null!;
    private Guid _accountId;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _services = _postgres.BuildWorkerServices();

        // Roda o slice para ter evidencias reais indexadas.
        var accounts = _services.GetRequiredService<IAccountRepository>();
        _accountId = await TestData.CreateAccountAsync(accounts);

        await TestData.EnqueueResearchAsync(
            _services.GetRequiredService<IUnitOfWorkFactory>(),
            _services.GetRequiredService<IResearchRunRepository>(),
            accounts,
            _services.GetRequiredService<IOutboxRepository>(),
            _accountId);

        await _services.GetRequiredService<OutboxDispatcher>()
            .DrainOnceAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private ISearchRepository Search => _services.GetRequiredService<ISearchRepository>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------ evidencias

    [Fact]
    public async Task Encontra_evidencia_por_termo_do_texto()
    {
        var hits = await Search.SearchEvidenceAsync("unidades", 20, Ct);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(_accountId, h.AccountId));
        Assert.All(hits, h => Assert.False(string.IsNullOrWhiteSpace(h.SourceUrl)));
    }

    [Fact]
    public async Task Busca_e_insensivel_a_acento()
    {
        // A configuracao portuguese_unaccent remove acentos antes do stemmer.
        var comAcento = await Search.SearchEvidenceAsync("concessionárias", 20, Ct);
        var semAcento = await Search.SearchEvidenceAsync("concessionarias", 20, Ct);

        Assert.NotEmpty(semAcento);
        Assert.Equal(semAcento.Count, comAcento.Count);
    }

    [Fact]
    public async Task Stemmer_resolve_plural()
    {
        var singular = await Search.SearchEvidenceAsync("loja", 20, Ct);
        var plural = await Search.SearchEvidenceAsync("lojas", 20, Ct);

        Assert.NotEmpty(singular);
        Assert.Equal(plural.Count, singular.Count);
    }

    [Fact]
    public async Task Expansao_de_sinonimo_alcanca_a_forma_verbal()
    {
        // Evidencia que so contem a forma VERBAL, nunca o substantivo.
        await InsertEvidenceAsync("O grupo esta expandindo a operacao para o litoral.");

        // O stemmer gera 'expansa' para o substantivo e 'expand' para o verbo:
        // stems diferentes, entao a busca crua nao casaria. Confirma-se aqui.
        Assert.False(await MatchesRawAsync("expansao", "O grupo esta expandindo a operacao para o litoral."));

        // Com a expansao de sinonimos da aplicacao, encontra.
        var hits = await Search.SearchEvidenceAsync("expansao", 20, Ct);

        Assert.Contains(hits, h => h.ClaimText.Contains("expandindo"));
    }

    /// <summary>Insere uma evidencia avulsa, reusando a primeira fonte gravada.</summary>
    private async Task InsertEvidenceAsync(string claimText)
    {
        await TestData.ScalarAsync<int>(_postgres.ConnectionString, """
            insert into evidence (account_id, source_id, claim_type, claim_text, confidence)
            select @AccountId, (select id from sources limit 1), 'expansion', @ClaimText, 0.8
            """, new { AccountId = _accountId, ClaimText = claimText });
    }

    /// <summary>Busca crua, sem a expansao de sinonimos da aplicacao.</summary>
    private async Task<bool> MatchesRawAsync(string query, string text) =>
        await TestData.ScalarAsync<bool>(_postgres.ConnectionString, """
            select to_tsvector('portuguese_unaccent', @Text)
                   @@ websearch_to_tsquery('portuguese_unaccent', @Query)
            """, new { Text = text, Query = query });

    [Fact]
    public async Task Devolve_trecho_com_termo_destacado()
    {
        var hits = await Search.SearchEvidenceAsync("unidades", 20, Ct);

        Assert.Contains(hits, h => h.Headline.Contains('[') && h.Headline.Contains(']'));
    }

    [Fact]
    public async Task Ordena_por_relevancia()
    {
        var hits = await Search.SearchEvidenceAsync("lojas unidades", 20, Ct);

        Assert.NotEmpty(hits);
        Assert.True(hits.SequenceEqual(hits.OrderByDescending(h => h.Rank)));
    }

    [Fact]
    public async Task Sintaxe_de_exclusao_funciona()
    {
        var todas = await Search.SearchEvidenceAsync("unidades", 20, Ct);
        var semJornal = await Search.SearchEvidenceAsync("unidades -jornal", 20, Ct);

        Assert.True(semJornal.Count < todas.Count);
    }

    [Fact]
    public async Task Termo_inexistente_nao_retorna_nada()
    {
        Assert.Empty(await Search.SearchEvidenceAsync("panificadora", 20, Ct));
    }

    // ----------------------------------------------------------------- contas

    [Fact]
    public async Task Encontra_conta_pelo_nome()
    {
        var hits = await Search.SearchAccountsAsync("vento sul", 20, Ct);

        Assert.Contains(hits, h => h.Id == _accountId);
    }

    [Fact]
    public async Task Encontra_conta_pelo_segmento_preenchido_na_pesquisa()
    {
        var hits = await Search.SearchAccountsAsync("dealer_group", 20, Ct);

        Assert.Contains(hits, h => h.Id == _accountId);
    }

    [Fact]
    public async Task Nome_pesa_mais_que_cidade_no_ranking()
    {
        var accounts = _services.GetRequiredService<IAccountRepository>();

        // Conta cujo NOME e "Bauru ..." vs a original, que so tem Bauru na cidade.
        await accounts.CreateFromCnpjAsync("11444777000161", "Bauru Motors", null, "SP", "Marilia");

        var hits = await Search.SearchAccountsAsync("bauru", 20, Ct);

        Assert.Equal("Bauru Motors", hits[0].Name);
    }

    // --------------------------------------------------- trigrama / account graph

    [Fact]
    public async Task Trigrama_encontra_razao_social_parecida()
    {
        var accounts = _services.GetRequiredService<IAccountRepository>();

        await accounts.CreateFromCnpjAsync("11444777000161", "Grupo Vento Sul LTDA", null, "SP", "Bauru");

        var similares = await Search.FindSimilarAccountsAsync(_accountId, 0.5m, 20, Ct);

        Assert.NotEmpty(similares);
        Assert.Equal("Grupo Vento Sul LTDA", similares[0].Name);

        // NameNormalizer remove o sufixo societario, entao os nomes normalizados
        // ficam identicos e a similaridade e total.
        Assert.Equal(1.0m, similares[0].Similarity);
        Assert.Equal("auto", similares[0].Recommendation);
    }

    [Fact]
    public async Task Trigrama_classifica_por_faixa_de_confianca()
    {
        var accounts = _services.GetRequiredService<IAccountRepository>();

        await accounts.CreateFromCnpjAsync("11444777000161", "Vento Sul Veiculos", null, "SP", "Bauru");

        var similares = await Search.FindSimilarAccountsAsync(_accountId, 0.3m, 20, Ct);
        var hit = similares.Single(s => s.Name == "Vento Sul Veiculos");

        // Faixas do §11: >=0.90 auto, >=0.75 provavel, abaixo disso revisao.
        Assert.InRange(hit.Similarity, 0.3m, 0.99m);
        Assert.Contains(hit.Recommendation, new[] { "provavel", "revisao" });
    }

    [Fact]
    public async Task Trigrama_ignora_nome_sem_relacao()
    {
        var accounts = _services.GetRequiredService<IAccountRepository>();

        await accounts.CreateFromCnpjAsync("11444777000161", "Padaria do Joao", null, "SP", "Bauru");

        var similares = await Search.FindSimilarAccountsAsync(_accountId, 0.5m, 20, Ct);

        Assert.DoesNotContain(similares, s => s.Name == "Padaria do Joao");
    }

    [Fact]
    public async Task Conta_nunca_aparece_como_similar_a_si_mesma()
    {
        var similares = await Search.FindSimilarAccountsAsync(_accountId, 0.1m, 20, Ct);

        Assert.DoesNotContain(similares, s => s.Id == _accountId);
    }
}
