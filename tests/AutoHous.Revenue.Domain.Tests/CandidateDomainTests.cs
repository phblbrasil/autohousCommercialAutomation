using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

/// <summary>
/// A derivação do domínio provável a partir do e-mail da Receita.
///
/// É uma suposição, e o custo de errar não é simétrico. Um domínio descartado à
/// toa só encolhe a amostra; um domínio errado aceito coloca o site de um
/// provedor de hospedagem dentro da distribuição do mercado e desloca todos os
/// percentis — que é justamente o número que o ADR-0013 usa para definir
/// severidade. Na dúvida, descarta.
/// </summary>
public class CandidateDomainTests
{
    [Theory]
    [InlineData("contato@grupoventosul.com.br", "grupoventosul.com.br")]
    [InlineData("VENDAS@AutoCenterSul.COM.BR", "autocentersul.com.br")]
    [InlineData("  financeiro@revendabc.com.br  ", "revendabc.com.br")]
    public void Email_corporativo_vira_dominio(string email, string esperado)
    {
        Assert.Equal(esperado, CandidateDomain.FromEmail(email));
    }

    /// <summary>
    /// Provedor pessoal não é site de ninguém — e é o caso mais comum da base:
    /// 76% das revendas operam em e-mail pessoal. Aceitá-los faria a
    /// distribuição do mercado ser medida em cima do gmail.com.
    /// </summary>
    [Theory]
    [InlineData("joao@gmail.com")]
    [InlineData("contato@hotmail.com")]
    [InlineData("vendas@uol.com.br")]
    [InlineData("loja@yahoo.com.br")]
    public void Provedor_pessoal_e_descartado(string email)
    {
        Assert.Null(CandidateDomain.FromEmail(email));
    }

    /// <summary>
    /// Hospedagem também não: o domínio é do provedor, e o site do cliente está
    /// em outro lugar. Sondar `locaweb.com.br` mediria a Locaweb.
    /// </summary>
    [Theory]
    [InlineData("contato@locaweb.com.br")]
    [InlineData("vendas@kinghost.com.br")]
    public void Provedor_de_hospedagem_e_descartado(string email)
    {
        Assert.Null(CandidateDomain.FromEmail(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("naoehemail")]
    [InlineData("sem@dominio")]
    [InlineData("@semlocal.com.br")]
    [InlineData("truncado@")]
    [InlineData("com espaco@dominio quebrado.com")]
    public void Lixo_devolve_nulo(string? email)
    {
        Assert.Null(CandidateDomain.FromEmail(email));
    }

    /// <summary>
    /// Parte das linhas da Receita traz mais de um endereço no mesmo campo. O
    /// primeiro é o que a empresa declarou primeiro.
    /// </summary>
    [Fact]
    public void Multiplos_enderecos_usam_o_primeiro()
    {
        Assert.Equal("primeira.com.br",
            CandidateDomain.FromEmail("contato@primeira.com.br;outro@segunda.com.br"));

        Assert.Equal("primeira.com.br",
            CandidateDomain.FromEmail("contato@primeira.com.br, outro@segunda.com.br"));
    }

    [Fact]
    public void Www_e_ponto_final_sao_removidos()
    {
        Assert.Equal("lojasul.com.br", CandidateDomain.FromEmail("contato@www.lojasul.com.br"));
        Assert.Equal("lojasul.com.br", CandidateDomain.FromEmail("contato@lojasul.com.br."));
    }

    [Fact]
    public void A_url_da_sonda_comeca_em_https()
    {
        // HTTPS primeiro; redirect resolve o resto, e a sonda segue até 5 saltos.
        Assert.Equal("https://lojasul.com.br", CandidateDomain.ToUrl("lojasul.com.br"));
    }
}
