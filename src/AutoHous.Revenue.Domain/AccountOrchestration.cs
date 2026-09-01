namespace AutoHous.Revenue.Domain;

/// <summary>O que fazer com esta conta agora.</summary>
public enum NextAction
{
    /// <summary>Nada, e nao por enquanto: a conta esta fora do funil automatico.</summary>
    Stop,

    /// <summary>Ha trabalho em voo. Reagendar seria empilhar run sobre run.</summary>
    Wait,

    Research,
    Audit,
    Score,
    MatchProducts,
    FindContacts,

    /// <summary>Pronta para abordagem: tem retrato, dor, produto e decisor.</summary>
    MarkReady,

    /// <summary>Nao ha proximo passo que melhore a conta. Sai da fila quente.</summary>
    Nurture
}

public sealed record OrchestrationDecision(NextAction Action, string Rationale);

/// <summary>
/// Retrato do que ja se sabe e do que ja se fez com uma conta.
///
/// Os campos <c>...At</c> importam mais que os <c>Has...</c>, e a diferenca e o
/// que impede um laco. "Tem contatos?" respondida com <c>false</c> depois de uma
/// busca que nao achou ninguem faria o Orchestrator pedir a mesma busca para
/// sempre. "Quando foi a ultima busca?" responde a pergunta certa: ja tentamos,
/// e o resultado foi vazio.
/// </summary>
public sealed record AccountProgress
{
    public required Guid AccountId { get; init; }
    public required AccountStatus Status { get; init; }

    /// <summary>Ha um research_run em <c>queued</c> ou <c>running</c> agora.</summary>
    public bool HasRunInFlight { get; init; }

    public bool HasDomain { get; init; }
    public decimal? ResearchCompleteness { get; init; }
    public DateTimeOffset? LastResearchedAt { get; init; }

    /// <summary>
    /// Quando o retrato vence. Sai de <c>accounts.next_research_at</c>, escrito
    /// pelo persister da pesquisa a cada execucao.
    /// </summary>
    public DateTimeOffset? NextResearchAt { get; init; }

    /// <summary>
    /// Ultima auditoria, tenha ela alcancado o site ou nao. Site fora do ar
    /// produz linha em <c>website_audits</c> de proposito, e por isso preenche
    /// este campo: sem ele, um dominio morto pediria auditoria a cada evento.
    /// </summary>
    public DateTimeOffset? LastAuditedAt { get; init; }

    public Guid? CurrentScoreId { get; init; }
    public DateTimeOffset? ScoredAt { get; init; }
    public short? Tier { get; init; }

    public Guid? ProductFitBatchId { get; init; }
    public DateTimeOffset? ProductFitAt { get; init; }

    /// <summary>Achado do Product Matcher que desaconselha abordar agora.</summary>
    public bool HasBlockingDisqualifier { get; init; }

    public DateTimeOffset? ContactsSearchedAt { get; init; }
    public bool HasDecisionMaker { get; init; }
}

/// <summary>
/// Orchestrator (A01), metade que decide.
///
/// A analise de lacunas registrou a distincao que justifica esta classe existir:
/// o <c>OutboxDispatcher</c> ROTEIA eventos por tipo, e o Orchestrator do frame
/// 05 DECIDE o proximo passo a partir do estado da conta. O roteador e
/// infraestrutura; o orquestrador e politica.
///
/// Enquanto a decisao morava no <c>switch</c> do dispatcher, ela dizia coisas do
/// tipo "pesquisa concluida significa pontuar" - o que e verdade, e e uma regra
/// de negocio escrita dentro de um adaptador. Pior: o dispatcher so enxergava o
/// evento que acabara de chegar, entao nao havia lugar de onde perguntar "esta
/// conta ja tem auditoria?". A cadeia era fixa por construcao.
///
/// Aqui a decisao e funcao pura do retrato inteiro. Duas consequencias praticas:
/// a ordem das etapas deixa de estar espalhada por cinco casos de uso, e uma
/// conta que chega pelo meio - importada ja com pesquisa, ou reprocessada depois
/// de um sinal novo - retoma no ponto certo em vez de recomecar.
/// </summary>
public static class AccountOrchestration
{
    /// <summary>
    /// Piso de completude para a pesquisa contar como feita. Abaixo disto o
    /// retrato nao sustenta auditoria nem fit: falta dominio, falta segmento, e
    /// o que sair depois herda a lacuna.
    /// </summary>
    public const decimal MinimumResearchCompleteness = 0.4m;

    /// <summary>
    /// Tier a partir do qual a conta nao segue para produto e contato. Tier 4 e
    /// a banda <c>nurture</c> do <see cref="OpportunityScore"/>: gastar uma
    /// chamada de modelo e uma busca de pessoa numa conta dessas e queimar
    /// orcamento que a fila quente precisa.
    /// </summary>
    public const short ColdTier = 4;

    public static OrchestrationDecision Decide(AccountProgress progress, DateTimeOffset now)
    {
        // 1. Contas fora do funil automatico. Regra dura da secao 18: cliente
        //    nunca recebe cold outbound, e suprimida e decisao humana que
        //    nenhum caso de uso desfaz.
        if (AccountStatusTransitions.BlocksOutbound(progress.Status))
        {
            return new OrchestrationDecision(NextAction.Stop,
                $"conta em {progress.Status.ToDbValue()}: fora do funil automatico");
        }

        // 2. Trabalho em voo. Sem esta guarda, dois eventos da mesma rajada -
        //    pesquisa concluida e sinal novo - pediriam duas auditorias da mesma
        //    conta, e as duas rodariam.
        if (progress.HasRunInFlight)
        {
            return new OrchestrationDecision(NextAction.Wait, "ja existe run em execucao para esta conta");
        }

        // 3. Sem retrato nao ha o que auditar nem o que pontuar.
        if (progress.LastResearchedAt is null)
        {
            return new OrchestrationDecision(NextAction.Research, "conta nunca pesquisada");
        }

        if (progress.ResearchCompleteness is { } completeness &&
            completeness < MinimumResearchCompleteness)
        {
            return new OrchestrationDecision(NextAction.Research,
                $"completude da pesquisa em {completeness:P0}, abaixo do piso de {MinimumResearchCompleteness:P0}");
        }

        // Retrato vencido. A data vem de `accounts.next_research_at`, que o
        // persister da pesquisa empurra para frente a cada execucao - e e essa
        // escrita que impede o laco: sem ela, uma conta vencida pediria pesquisa
        // a cada evento que chegasse.
        if (progress.NextResearchAt is { } due && due <= now)
        {
            return new OrchestrationDecision(NextAction.Research,
                $"retrato vencido em {due:yyyy-MM-dd}: refazer antes de decidir o resto");
        }

        // 4. Auditoria. So faz sentido com dominio - quem descobre o dominio e a
        //    pesquisa, e por isso este passo vem depois dela e nao antes.
        if (progress.HasDomain && progress.LastAuditedAt is null)
        {
            return new OrchestrationDecision(NextAction.Audit, "conta com dominio e sem auditoria de site");
        }

        // 5. Score desatualizado em relacao ao fato mais novo. A comparacao e
        //    por data e nao por existencia: uma auditoria que chegou depois do
        //    ultimo score mudou Technology Pain, e o numero na fila esta velho.
        var newestFact = Newest(progress.LastResearchedAt, progress.LastAuditedAt);

        if (progress.ScoredAt is null)
        {
            return new OrchestrationDecision(NextAction.Score, "conta com pesquisa e sem score");
        }

        if (newestFact is { } fact && progress.ScoredAt < fact)
        {
            return new OrchestrationDecision(NextAction.Score,
                $"fato novo em {fact:yyyy-MM-dd HH:mm} posterior ao score de {progress.ScoredAt:yyyy-MM-dd HH:mm}");
        }

        // 6. Conta fria para de consumir agente aqui. Continua no banco, com
        //    score e auditoria, e volta a fila quando um sinal novo a repontuar.
        if (progress.Tier >= ColdTier)
        {
            return new OrchestrationDecision(NextAction.Nurture,
                $"tier {progress.Tier}: abaixo do corte para produto e contato");
        }

        // 7. Fit de produto. Ancorado na safra de score: um score novo pode
        //    mudar qual e a porta de entrada.
        if (progress.ProductFitAt is null)
        {
            return new OrchestrationDecision(NextAction.MatchProducts, "conta pontuada e sem fit de produto");
        }

        if (progress.ScoredAt is { } scored && progress.ProductFitAt < scored)
        {
            return new OrchestrationDecision(NextAction.MatchProducts,
                $"score de {scored:yyyy-MM-dd HH:mm} posterior ao fit de {progress.ProductFitAt:yyyy-MM-dd HH:mm}");
        }

        // 8. Desqualificador achado pelo Product Matcher. Tira da fila quente e
        //    NAO suprime: suppression e decisao humana (Regra 2), e um agente
        //    que conclui "esta empresa encerrou atividade" a partir de uma
        //    pagina desatualizada nao pode banir a conta sozinho.
        if (progress.HasBlockingDisqualifier)
        {
            return new OrchestrationDecision(NextAction.Nurture,
                "desqualificador registrado: revisao humana antes de abordar");
        }

        // 9. Contatos. A pergunta e "ja procuramos?", e nao "temos?": uma busca
        //    que voltou vazia e resultado, e repeti-la a cada evento gastaria
        //    uma chamada de modelo para reconfirmar a mesma ausencia.
        if (progress.ContactsSearchedAt is null)
        {
            return new OrchestrationDecision(NextAction.FindContacts, "fit definido e nenhuma busca de contatos feita");
        }

        if (progress.ProductFitAt is { } fit && progress.ContactsSearchedAt < fit)
        {
            return new OrchestrationDecision(NextAction.FindContacts,
                "fit novo desde a ultima busca: as personas a procurar mudaram");
        }

        // 10. Pronta, ou nao ha mais nada automatico a fazer.
        return progress.HasDecisionMaker
            ? new OrchestrationDecision(NextAction.MarkReady, "retrato, dor, produto e decisor: pronta para abordagem")
            : new OrchestrationDecision(NextAction.Nurture, "busca de contatos concluida sem decisor identificado");
    }

    private static DateTimeOffset? Newest(params DateTimeOffset?[] dates) =>
        dates.Where(d => d is not null).DefaultIfEmpty(null).Max();
}
