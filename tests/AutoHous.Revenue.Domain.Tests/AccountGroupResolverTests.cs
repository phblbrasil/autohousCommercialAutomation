namespace AutoHous.Revenue.Domain.Tests;

public class AccountGroupResolverTests
{
    private static NormalizedCompany Company(
        string cnpj = "11222333000181", string name = "VENTO SUL VEICULOS", string? uf = "SP") =>
        CompanyNormalizer.Normalize(new RawCompanyFields
        {
            Cnpj = cnpj,
            RazaoSocial = name,
            CnaePrincipal = "4511101",
            SituacaoCadastral = "02",
            Uf = uf,
            Municipio = "Bauru"
        }).Company!;

    private static AccountGroupCandidate Candidate(
        decimal similarity, string? uf = "SP", string[]? roots = null) => new()
        {
            AccountId = Guid.NewGuid(),
            Name = "Vento Sul Veiculos",
            NormalizedName = "VENTO SUL VEICULOS",
            Uf = uf,
            CnpjRoots = roots ?? [],
            NameSimilarity = similarity
        };

    [Fact]
    public void Sem_candidatos_cria_conta_com_confianca_total()
    {
        var decision = AccountGroupResolver.Resolve(Company(), []);

        Assert.Equal(AccountGroupAction.CreateAccount, decision.Action);
        Assert.Equal(1.00m, decision.Confidence);
        Assert.Equal("no_candidate", decision.Reason);
    }

    /// <summary>
    /// Confundir "nenhum candidato" com "baixa confianca" encheria a fila de
    /// revisao humana de conta legitima e nova — o oposto do que a fila serve.
    /// </summary>
    [Fact]
    public void Candidato_abaixo_da_faixa_de_revisao_e_ignorado()
    {
        var decision = AccountGroupResolver.Resolve(Company(), [Candidate(0.40m)]);

        Assert.Equal(AccountGroupAction.CreateAccount, decision.Action);
        Assert.Equal(1.00m, decision.Confidence);
    }

    [Fact]
    public void Mesma_raiz_de_cnpj_vence_tudo()
    {
        // Filial e matriz compartilham os oito primeiros digitos por definicao
        // da Receita: nao ha julgamento a fazer, nem que o nome nao pareca.
        var candidato = Candidate(0.10m, uf: "RS", roots: ["11222333"]);

        var decision = AccountGroupResolver.Resolve(Company(), [candidato]);

        Assert.Equal(AccountGroupAction.AttachToExisting, decision.Action);
        Assert.Equal(candidato.AccountId, decision.AccountId);
        Assert.Equal(1.00m, decision.Confidence);
        Assert.Equal("cnpj_root", decision.Reason);
    }

    [Fact]
    public void Raiz_diferente_nao_e_tratada_como_identidade()
    {
        var decision = AccountGroupResolver.Resolve(
            Company(), [Candidate(0.30m, roots: ["99888777"])]);

        Assert.Equal(AccountGroupAction.CreateAccount, decision.Action);
    }

    [Fact]
    public void Nome_muito_parecido_na_mesma_uf_une_automaticamente()
    {
        var candidato = Candidate(0.93m, uf: "SP");

        var decision = AccountGroupResolver.Resolve(Company(uf: "SP"), [candidato]);

        Assert.Equal(AccountGroupAction.AttachToExisting, decision.Action);
        Assert.Equal(candidato.AccountId, decision.AccountId);
        Assert.Equal("name_and_uf", decision.Reason);
    }

    /// <summary>
    /// Grupos automotivos sao regionais. "Vento Sul Veiculos" em SP e em RS sao,
    /// quase sempre, empresas diferentes com um nome generico parecido — e unir
    /// as duas custa mais caro do que perguntar.
    /// </summary>
    [Fact]
    public void Nome_identico_em_outra_uf_vai_para_revisao()
    {
        var decision = AccountGroupResolver.Resolve(
            Company(uf: "SP"), [Candidate(0.98m, uf: "RS")]);

        Assert.Equal(AccountGroupAction.SendToReview, decision.Action);
        Assert.Equal("name_match_other_uf", decision.Reason);
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(0.80)]
    [InlineData(0.89)]
    public void Faixa_intermediaria_vai_para_revisao(double similarity)
    {
        var decision = AccountGroupResolver.Resolve(
            Company(), [Candidate((decimal)similarity)]);

        Assert.Equal(AccountGroupAction.SendToReview, decision.Action);
        Assert.Equal("name_similarity", decision.Reason);
    }

    [Fact]
    public void Escolhe_o_candidato_mais_parecido()
    {
        var melhor = Candidate(0.95m);

        var decision = AccountGroupResolver.Resolve(
            Company(), [Candidate(0.78m), melhor, Candidate(0.80m)]);

        Assert.Equal(melhor.AccountId, decision.AccountId);
    }

    [Fact]
    public void Empresa_sem_uf_nao_une_automaticamente_por_nome()
    {
        // Sem UF nao da para confirmar a regionalidade; a decisao sobe para o humano.
        var decision = AccountGroupResolver.Resolve(
            Company(uf: null), [Candidate(0.97m, uf: "SP")]);

        Assert.Equal(AccountGroupAction.SendToReview, decision.Action);
    }
}
