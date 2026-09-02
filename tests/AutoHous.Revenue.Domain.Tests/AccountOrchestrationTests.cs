using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

/// <summary>
/// A politica do Orchestrator (A01).
///
/// O que estes testes mais protegem nao e a ordem das etapas - essa e facil de
/// ler no codigo - e sim as guardas que impedem LACO. Um orquestrador que pede
/// de novo o passo que acabou de falhar, ou que reexecuta uma busca que voltou
/// vazia, nao produz erro visivel: produz uma fila que gira, gastando modelo,
/// ate alguem notar a fatura.
/// </summary>
public class AccountOrchestrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static AccountProgress Conta(AccountStatus status = AccountStatus.Discovered) => new()
    {
        AccountId = Guid.NewGuid(),
        Status = status
    };

    private static NextAction Decide(AccountProgress progress) =>
        AccountOrchestration.Decide(progress, Now).Action;

    // ------------------------------------------------------------- fronteiras

    [Theory]
    [InlineData(AccountStatus.Suppressed)]
    [InlineData(AccountStatus.Customer)]
    [InlineData(AccountStatus.Rejected)]
    public void Conta_fora_do_funil_para(AccountStatus status)
    {
        // Regra dura da secao 18: cliente nunca recebe cold outbound, e
        // suppression e decisao humana que nenhum caso de uso desfaz. A conta
        // pode estar completa - com score, fit e decisor - e ainda assim para.
        var progress = Conta(status) with
        {
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.9m,
            ScoredAt = Now.AddHours(-1),
            Tier = 1,
            HasDecisionMaker = true
        };

        Assert.Equal(NextAction.Stop, Decide(progress));
    }

    /// <summary>
    /// A guarda mais importante contra trabalho duplicado. Sem ela, dois eventos
    /// da mesma rajada - pesquisa concluida e sinal novo - pediriam duas
    /// auditorias da mesma conta, e as duas rodariam.
    /// </summary>
    [Fact]
    public void Run_em_voo_faz_esperar_em_vez_de_empilhar()
    {
        var progress = Conta(AccountStatus.Researching) with { HasRunInFlight = true };

        Assert.Equal(NextAction.Wait, Decide(progress));
    }

    // ---------------------------------------------------------- ordem do funil

    [Fact]
    public void Conta_nunca_pesquisada_vai_para_pesquisa()
    {
        Assert.Equal(NextAction.Research, Decide(Conta()));
    }

    [Fact]
    public void Pesquisa_rasa_e_refeita_antes_de_qualquer_outra_coisa()
    {
        var progress = Conta(AccountStatus.Researched) with
        {
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.2m,
            HasDomain = true
        };

        Assert.Equal(NextAction.Research, Decide(progress));
    }

    /// <summary>
    /// A guarda contra o laco mais caro que este orquestrador consegue produzir.
    ///
    /// A cadeia se alimenta sozinha: `research.completed` chega, a completude
    /// continua abaixo do piso, pede-se pesquisa de novo, o persister emite
    /// `research.completed` outra vez. Nao ha evento externo nenhum sustentando
    /// o giro - e nao ha erro, nem log vermelho, nem run falhado. So fatura.
    ///
    /// E cai exatamente onde dói mais: `research_completeness` e declarada pelo
    /// PROPRIO agente, e uma revenda sem presenca digital devolve 0,25 com toda
    /// honestidade. As contas onde a pesquisa rende menos eram as que mais
    /// rodavam.
    ///
    /// `next_research_at` e a guarda que ja existia para o retrato vencido; o
    /// ramo da completude passava por cima dela.
    /// </summary>
    [Fact]
    public void Pesquisa_rasa_dentro_do_prazo_nao_repesquisa_em_laco()
    {
        var progress = Conta(AccountStatus.Researched) with
        {
            LastResearchedAt = Now.AddMinutes(-1),
            ResearchCompleteness = 0.2m,
            NextResearchAt = Now.AddDays(30),
            HasDomain = true
        };

        var decision = AccountOrchestration.Decide(progress, Now);

        Assert.Equal(NextAction.Nurture, decision.Action);
        Assert.NotEqual(NextAction.Research, decision.Action);
    }

    /// <summary>
    /// Vencido o prazo, a conta rasa volta a ser pesquisada: a guarda acima e
    /// cooldown, e nao desistencia.
    /// </summary>
    [Fact]
    public void Pesquisa_rasa_com_prazo_vencido_e_refeita()
    {
        var progress = Conta(AccountStatus.Researched) with
        {
            LastResearchedAt = Now.AddDays(-40),
            ResearchCompleteness = 0.2m,
            NextResearchAt = Now.AddDays(-1),
            HasDomain = true
        };

        Assert.Equal(NextAction.Research, Decide(progress));
    }

    /// <summary>
    /// Auditoria depende de dominio, e quem descobre o dominio e a pesquisa.
    /// Sem essa precedencia, o auditor seria chamado para uma conta sem site
    /// conhecido e falharia por pre-condicao a cada evento.
    /// </summary>
    [Fact]
    public void Conta_sem_dominio_pula_a_auditoria_e_vai_pontuar()
    {
        var progress = Conta(AccountStatus.Researched) with
        {
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.8m,
            HasDomain = false
        };

        Assert.Equal(NextAction.Score, Decide(progress));
    }

    [Fact]
    public void Conta_com_dominio_e_sem_auditoria_vai_auditar()
    {
        var progress = Conta(AccountStatus.Researched) with
        {
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.8m,
            HasDomain = true
        };

        Assert.Equal(NextAction.Audit, Decide(progress));
    }

    /// <summary>
    /// A guarda que impede o laco da auditoria: site fora do ar produz linha em
    /// <c>website_audits</c> de proposito, e e isso que preenche
    /// <c>LastAuditedAt</c>. Sem ela, um dominio morto pediria auditoria para
    /// sempre.
    /// </summary>
    [Fact]
    public void Auditoria_ja_tentada_nao_e_pedida_de_novo()
    {
        var progress = Conta(AccountStatus.Researched) with
        {
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.8m,
            HasDomain = true,
            LastAuditedAt = Now.AddHours(-2)
        };

        Assert.Equal(NextAction.Score, Decide(progress));
    }

    [Fact]
    public void Fato_novo_depois_do_score_manda_repontuar()
    {
        var progress = Conta(AccountStatus.Scored) with
        {
            LastResearchedAt = Now.AddDays(-2),
            ResearchCompleteness = 0.8m,
            HasDomain = true,
            ScoredAt = Now.AddHours(-3),

            // A auditoria chegou DEPOIS do ultimo score: Technology Pain mudou,
            // e o numero que ordena a fila esta velho.
            LastAuditedAt = Now.AddHours(-1),
            Tier = 2
        };

        Assert.Equal(NextAction.Score, Decide(progress));
    }

    // -------------------------------------------------------------- corte frio

    [Fact]
    public void Tier_frio_nao_gasta_agente_com_produto_nem_contato()
    {
        var progress = Conta(AccountStatus.Scored) with
        {
            LastResearchedAt = Now.AddDays(-2),
            ResearchCompleteness = 0.8m,
            HasDomain = true,
            LastAuditedAt = Now.AddDays(-2),
            ScoredAt = Now.AddHours(-1),
            Tier = 4
        };

        Assert.Equal(NextAction.Nurture, Decide(progress));
    }

    // ------------------------------------------------------ produto e contatos

    private static AccountProgress Pontuada(short tier = 2) => Conta(AccountStatus.Scored) with
    {
        LastResearchedAt = Now.AddDays(-2),
        ResearchCompleteness = 0.8m,
        HasDomain = true,
        LastAuditedAt = Now.AddDays(-2),
        CurrentScoreId = Guid.NewGuid(),
        ScoredAt = Now.AddHours(-1),
        Tier = tier
    };

    [Fact]
    public void Conta_pontuada_sem_fit_vai_para_o_product_matcher()
    {
        Assert.Equal(NextAction.MatchProducts, Decide(Pontuada()));
    }

    [Fact]
    public void Score_mais_novo_que_o_fit_refaz_o_fit()
    {
        var progress = Pontuada() with
        {
            ProductFitBatchId = Guid.NewGuid(),
            ProductFitAt = Now.AddHours(-5)
        };

        Assert.Equal(NextAction.MatchProducts, Decide(progress));
    }

    /// <summary>
    /// Desqualificador tira da fila quente e NAO suprime. A distincao e a Regra
    /// 2: um agente que conclui "esta empresa encerrou atividade" a partir de
    /// uma pagina desatualizada nao pode banir a conta sozinho.
    /// </summary>
    [Fact]
    public void Desqualificador_manda_para_nurture_e_nao_para_suppression()
    {
        var progress = Pontuada() with
        {
            ProductFitBatchId = Guid.NewGuid(),
            ProductFitAt = Now.AddMinutes(-30),
            HasBlockingDisqualifier = true
        };

        var decision = AccountOrchestration.Decide(progress, Now);

        Assert.Equal(NextAction.Nurture, decision.Action);
        Assert.NotEqual(NextAction.Stop, decision.Action);
    }

    /// <summary>
    /// A safra vigente com porta de entrada: fit calculado E algum produto
    /// acima do corte. E o unico estado a partir do qual vale gastar uma busca
    /// de pessoas.
    /// </summary>
    private static AccountProgress ComFit(short tier = 2) => Pontuada(tier) with
    {
        ProductFitBatchId = Guid.NewGuid(),
        ProductFitAt = Now.AddMinutes(-30),
        HasRecommendedEntry = true
    };

    [Fact]
    public void Conta_com_fit_e_sem_busca_de_contatos_procura_pessoas()
    {
        Assert.Equal(NextAction.FindContacts, Decide(ComFit()));
    }

    /// <summary>
    /// Fit calculado nao e fit encontrado, e a diferenca custa uma chamada de
    /// modelo.
    ///
    /// O Product Matcher grava a aritmetica dos cinco produtos mesmo quando
    /// nenhum passa do corte - e ela que da ordem a fila -, entao
    /// <c>ProductFitAt</c> fica preenchido nos dois casos e sozinho nao
    /// distingue "achei o produto" de "olhei e nao ha". Sem esta guarda, uma
    /// conta cujo melhor produto pontuou 30 seguia para o People Finder atras do
    /// decisor de uma conversa que a plataforma ja tinha julgado inexistente.
    ///
    /// O corte de tier nao cobre o caso: tier 3 passa pelo passo 6 e chega aqui.
    /// </summary>
    [Fact]
    public void Fit_sem_produto_acima_do_corte_nao_gasta_busca_de_pessoas()
    {
        var progress = Pontuada(tier: 3) with
        {
            ProductFitBatchId = Guid.NewGuid(),
            ProductFitAt = Now.AddMinutes(-30),
            HasRecommendedEntry = false
        };

        var decision = AccountOrchestration.Decide(progress, Now);

        Assert.Equal(NextAction.Nurture, decision.Action);
        Assert.NotEqual(NextAction.FindContacts, decision.Action);
    }

    /// <summary>
    /// A guarda que impede o laco do People Finder, e a que mais custaria se
    /// faltasse: uma busca que voltou vazia e um RESULTADO. Perguntar "temos
    /// contatos?" em vez de "ja procuramos?" faria a conta sem ninguem
    /// localizavel refazer a mesma busca a cada evento, para sempre.
    /// </summary>
    [Fact]
    public void Busca_vazia_ja_feita_nao_e_repetida()
    {
        var progress = ComFit() with
        {
            ContactsSearchedAt = Now.AddMinutes(-10),
            HasDecisionMaker = false
        };

        var decision = AccountOrchestration.Decide(progress, Now);

        Assert.Equal(NextAction.Nurture, decision.Action);
        Assert.NotEqual(NextAction.FindContacts, decision.Action);
    }

    [Fact]
    public void Fit_novo_desde_a_ultima_busca_refaz_a_busca()
    {
        // As personas a procurar saem do produto de entrada; fit novo pode
        // mudar quem procurar.
        var progress = ComFit() with
        {
            ProductFitAt = Now.AddMinutes(-10),
            ContactsSearchedAt = Now.AddDays(-3),
            HasDecisionMaker = true
        };

        Assert.Equal(NextAction.FindContacts, Decide(progress));
    }

    [Fact]
    public void Conta_completa_com_decisor_fica_pronta()
    {
        var progress = ComFit() with
        {
            ContactsSearchedAt = Now.AddMinutes(-10),
            HasDecisionMaker = true
        };

        Assert.Equal(NextAction.MarkReady, Decide(progress));
    }

    // ------------------------------------------------------------- reprocesso

    /// <summary>
    /// Retrato vencido refaz a pesquisa. A data vem de
    /// <c>accounts.next_research_at</c>, que o persister da pesquisa empurra
    /// para frente a cada execucao - e e essa escrita que impede o laco.
    /// </summary>
    [Fact]
    public void Retrato_vencido_volta_para_a_pesquisa()
    {
        var progress = ComFit() with
        {
            NextResearchAt = Now.AddDays(-1),
            ProductFitAt = Now.AddMinutes(-10),
            ContactsSearchedAt = Now.AddMinutes(-5),
            HasDecisionMaker = true
        };

        Assert.Equal(NextAction.Research, Decide(progress));
    }

    [Fact]
    public void Retrato_no_prazo_segue_o_funil()
    {
        var progress = ComFit() with
        {
            NextResearchAt = Now.AddDays(20),
            ProductFitAt = Now.AddMinutes(-10),
            ContactsSearchedAt = Now.AddMinutes(-5),
            HasDecisionMaker = true
        };

        Assert.Equal(NextAction.MarkReady, Decide(progress));
    }

    /// <summary>
    /// Toda decisao vem com justificativa. E o unico rastro de por que uma conta
    /// parou onde parou - o dispatcher a registra em log, e sem ela "esta conta
    /// nao anda" nao tem investigacao possivel.
    /// </summary>
    [Fact]
    public void Toda_decisao_carrega_justificativa()
    {
        AccountProgress[] cenarios =
        [
            Conta(),
            Conta(AccountStatus.Suppressed),
            Conta(AccountStatus.Researching) with { HasRunInFlight = true },
            Pontuada(),
            Pontuada(4),
            Pontuada() with { ProductFitAt = Now.AddMinutes(-1), ContactsSearchedAt = Now, HasDecisionMaker = true }
        ];

        Assert.All(cenarios, c =>
            Assert.False(string.IsNullOrWhiteSpace(AccountOrchestration.Decide(c, Now).Rationale)));
    }
}
