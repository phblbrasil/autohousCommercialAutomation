using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

public class IdempotencyKeyTests
{
    private static readonly Guid Account = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Mesma_conta_e_mesmo_run_produzem_a_mesma_chave()
    {
        Assert.Equal(
            IdempotencyKey.ForResearch(Account, RunA),
            IdempotencyKey.ForResearch(Account, RunA));
    }

    [Fact]
    public void Runs_diferentes_produzem_chaves_diferentes()
    {
        // Este e o ponto: um retry apos falha cria um novo research_run e portanto
        // uma nova chave. A sugestao do blueprint (chave mensal por conta) tornaria
        // o retry impossivel dentro do mesmo mes.
        Assert.NotEqual(
            IdempotencyKey.ForResearch(Account, RunA),
            IdempotencyKey.ForResearch(Account, RunB));
    }

    [Fact]
    public void Chave_de_pesquisa_e_de_conclusao_nao_colidem()
    {
        Assert.NotEqual(
            IdempotencyKey.ForResearch(Account, RunA),
            IdempotencyKey.ForResearchCompleted(Account, RunA));
    }

    [Fact]
    public void Chave_de_outreach_segue_o_formato_da_secao_25()
    {
        var contact = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var key = IdempotencyKey.ForOutreach(contact, "frontcar-warmup", 2, "email");

        Assert.Equal("email:44444444444444444444444444444444:frontcar-warmup:2", key);
    }
}
