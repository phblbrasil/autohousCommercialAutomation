using AutoHous.Revenue.Domain;
using AutoHous.Revenue.WebAudit;

namespace AutoHous.Revenue.WebAudit.Tests;

/// <summary>
/// A leitura do <c>robots.txt</c> para a única pergunta que a auditoria faz:
/// quais rastreadores de IA estão bloqueados da raiz.
///
/// Cada teste aqui corresponde a uma forma de errar que inverte o diagnóstico —
/// acusar bloqueio onde há permissão, ou o contrário. Como o achado vira
/// argumento comercial ("seu site não pode ser lido quando alguém pergunta ao
/// ChatGPT onde comprar um carro"), errar aqui é pior que não medir.
/// </summary>
public class RobotsTxtTests
{
    [Fact]
    public void Site_aberto_nao_bloqueia_ninguem()
    {
        var blocked = RobotsTxt.BlockedAiCrawlers("User-agent: *\nDisallow:\n");

        Assert.Empty(blocked);
    }

    [Fact]
    public void Disallow_barra_no_coringa_bloqueia_todos()
    {
        var blocked = RobotsTxt.BlockedAiCrawlers("User-agent: *\nDisallow: /\n");

        Assert.Equal(AiCrawlers.All.Count, blocked.Count);
    }

    [Fact]
    public void Bloqueio_nomeado_atinge_so_quem_foi_nomeado()
    {
        var blocked = RobotsTxt.BlockedAiCrawlers("""
            User-agent: *
            Disallow:

            User-agent: GPTBot
            Disallow: /
            """);

        Assert.Equal(["GPTBot"], blocked);
    }

    /// <summary>
    /// Regra da especificação que, se ignorada, inverte o resultado: agentes em
    /// linhas consecutivas formam **um** grupo, e as diretivas abaixo valem para
    /// todos. Lendo par a par, o segundo agente herdaria a regra do bloco
    /// seguinte — ou nenhuma.
    /// </summary>
    [Fact]
    public void Agentes_consecutivos_compartilham_o_mesmo_grupo()
    {
        var blocked = RobotsTxt.BlockedAiCrawlers("""
            User-agent: GPTBot
            User-agent: ClaudeBot
            User-agent: CCBot
            Disallow: /
            """);

        Assert.Equal(3, blocked.Count);
        Assert.Contains("GPTBot", blocked);
        Assert.Contains("ClaudeBot", blocked);
        Assert.Contains("CCBot", blocked);
    }

    /// <summary>
    /// A outra metade da mesma regra: o grupo específico vence o coringa,
    /// inclusive para LIBERAR. Um site que bloqueia tudo em <c>*</c> e abre
    /// exceção para o GPTBot **permite** o GPTBot — considerar só o coringa
    /// acusaria bloqueio onde há permissão explícita.
    /// </summary>
    [Fact]
    public void Grupo_especifico_vence_o_coringa_inclusive_para_liberar()
    {
        var blocked = RobotsTxt.BlockedAiCrawlers("""
            User-agent: *
            Disallow: /

            User-agent: GPTBot
            Allow: /
            """);

        Assert.DoesNotContain("GPTBot", blocked);
        Assert.Contains("CCBot", blocked);
    }

    [Fact]
    public void Comentario_no_fim_da_linha_nao_atrapalha()
    {
        var blocked = RobotsTxt.BlockedAiCrawlers("""
            # bloqueia robo de IA
            User-agent: GPTBot   # o da OpenAI
            Disallow: /          # tudo
            """);

        Assert.Equal(["GPTBot"], blocked);
    }

    [Fact]
    public void Nome_do_agente_e_insensivel_a_caixa()
    {
        var blocked = RobotsTxt.BlockedAiCrawlers("User-agent: gptbot\nDisallow: /\n");

        Assert.Equal(["GPTBot"], blocked);
    }

    /// <summary>
    /// Bloquear treino e bloquear busca não são o mesmo fato, e a diferença é o
    /// que separa um diagnóstico acionável de um alarme.
    ///
    /// Recusar <c>CCBot</c> é decisão legítima de muita empresa. Recusar
    /// <c>OAI-SearchBot</c> tira a loja do resultado que o comprador vê enquanto
    /// pergunta onde achar o carro — e quase sempre ninguém decidiu isso.
    /// </summary>
    [Fact]
    public void Bloqueio_de_treino_nao_e_contado_como_bloqueio_de_busca()
    {
        var soTreino = RobotsTxt.BlockedAiCrawlers("""
            User-agent: CCBot
            User-agent: GPTBot
            Disallow: /
            """);

        Assert.Equal(2, soTreino.Count);
        Assert.Equal(0, AiCrawlers.CountSearch(soTreino));

        var comBusca = RobotsTxt.BlockedAiCrawlers("""
            User-agent: OAI-SearchBot
            Disallow: /
            """);

        Assert.Equal(1, AiCrawlers.CountSearch(comBusca));
    }
}
