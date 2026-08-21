using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

public class AccountStatusTransitionsTests
{
    [Theory]
    [InlineData(AccountStatus.Discovered, AccountStatus.Researching)]
    [InlineData(AccountStatus.Researching, AccountStatus.Researched)]
    [InlineData(AccountStatus.Researched, AccountStatus.Scored)]
    [InlineData(AccountStatus.Scored, AccountStatus.Ready)]
    [InlineData(AccountStatus.Ready, AccountStatus.Contacted)]
    [InlineData(AccountStatus.Contacted, AccountStatus.Engaged)]
    [InlineData(AccountStatus.Engaged, AccountStatus.Customer)]
    public void Permite_o_caminho_feliz(AccountStatus from, AccountStatus to)
    {
        Assert.True(AccountStatusTransitions.CanTransition(from, to));
    }

    [Fact]
    public void Permite_volta_para_discovered_quando_a_pesquisa_falha()
    {
        Assert.True(AccountStatusTransitions.CanTransition(
            AccountStatus.Researching, AccountStatus.Discovered));
    }

    [Theory]
    [InlineData(AccountStatus.Discovered, AccountStatus.Contacted)]  // pula pesquisa e score
    [InlineData(AccountStatus.Discovered, AccountStatus.Customer)]
    [InlineData(AccountStatus.Researching, AccountStatus.Ready)]
    [InlineData(AccountStatus.Scored, AccountStatus.Engaged)]
    public void Bloqueia_saltos_de_etapa(AccountStatus from, AccountStatus to)
    {
        Assert.False(AccountStatusTransitions.CanTransition(from, to));
        Assert.Throws<InvalidAccountTransitionException>(
            () => AccountStatusTransitions.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(AccountStatus.Ready)]
    [InlineData(AccountStatus.Contacted)]
    [InlineData(AccountStatus.Engaged)]
    [InlineData(AccountStatus.Customer)]
    [InlineData(AccountStatus.Researched)]
    public void Suppressed_e_alcancavel_de_qualquer_estado_ativo(AccountStatus from)
    {
        // Um unsubscribe pode chegar a qualquer momento (secao 18).
        Assert.True(AccountStatusTransitions.CanTransition(from, AccountStatus.Suppressed));
    }

    [Theory]
    [InlineData(AccountStatus.Ready)]
    [InlineData(AccountStatus.Contacted)]
    [InlineData(AccountStatus.Discovered)]
    [InlineData(AccountStatus.Customer)]
    public void Suppressed_e_terminal_na_maquina_automatica(AccountStatus to)
    {
        Assert.False(AccountStatusTransitions.CanTransition(AccountStatus.Suppressed, to));
    }

    [Fact]
    public void Customer_nunca_volta_para_cadencia_fria()
    {
        // Regra dura da secao 18.
        Assert.False(AccountStatusTransitions.CanTransition(AccountStatus.Customer, AccountStatus.Ready));
        Assert.False(AccountStatusTransitions.CanTransition(AccountStatus.Customer, AccountStatus.Contacted));
        Assert.True(AccountStatusTransitions.CanTransition(AccountStatus.Customer, AccountStatus.Suppressed));
    }

    [Theory]
    [InlineData(AccountStatus.Suppressed)]
    [InlineData(AccountStatus.Customer)]
    [InlineData(AccountStatus.Rejected)]
    public void BlocksOutbound_cobre_os_estados_proibidos(AccountStatus status)
    {
        Assert.True(AccountStatusTransitions.BlocksOutbound(status));
    }

    [Fact]
    public void Transicao_para_o_mesmo_estado_e_no_op_valido()
    {
        // Reprocessar um evento nao pode explodir por "transicao invalida".
        Assert.True(AccountStatusTransitions.CanTransition(
            AccountStatus.Researching, AccountStatus.Researching));
    }

    [Fact]
    public void Todo_estado_do_enum_esta_mapeado()
    {
        foreach (var status in Enum.GetValues<AccountStatus>())
        {
            // AllowedFrom devolve vazio para terminais, mas nunca deve lancar.
            _ = AccountStatusTransitions.AllowedFrom(status);
        }
    }
}
