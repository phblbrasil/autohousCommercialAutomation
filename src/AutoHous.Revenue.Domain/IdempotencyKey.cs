namespace AutoHous.Revenue.Domain;

/// <summary>
/// Construcao centralizada das chaves de idempotencia (secao 25).
///
/// Decisao importante: idempotencia e cooldown sao conceitos SEPARADOS. A chave
/// identifica esta execucao especifica; a janela de repesquisa e regra de
/// negocio checada na API. A sugestao do blueprint
/// (<c>research:{account}:v1:2026-08</c>) funde os dois e, como efeito colateral,
/// impediria o retry de um run que falhou dentro do mesmo mes.
/// </summary>
public static class IdempotencyKey
{
    public static string ForResearch(Guid accountId, Guid researchRunId) =>
        $"research:{accountId:N}:{researchRunId:N}";

    public static string ForResearchCompleted(Guid accountId, Guid researchRunId) =>
        $"research-completed:{accountId:N}:{researchRunId:N}";

    /// <summary>
    /// Score e recalculavel por natureza: chega um sinal novo, o numero muda. A
    /// chave e por minuto para que dois eventos disparados na mesma rajada -
    /// pesquisa concluida e sinal novo, por exemplo - nao gerem duas linhas
    /// identicas em account_scores, sem impedir o recalculo de amanha.
    /// </summary>
    public static string ForScore(Guid accountId, DateTimeOffset calculatedAt) =>
        $"score:{accountId:N}:{calculatedAt:yyyyMMddHHmm}";

    public static string ForAudit(Guid accountId, Guid researchRunId) =>
        $"audit:{accountId:N}:{researchRunId:N}";

    public static string ForAuditCompleted(Guid accountId, Guid researchRunId) =>
        $"audit-completed:{accountId:N}:{researchRunId:N}";

    /// <summary>
    /// Comando de recalculo emitido pelo Orchestrator. Mesma janela por minuto
    /// do <see cref="ForScore"/>, e pelo mesmo motivo: pesquisa e auditoria
    /// concluindo na mesma rajada devem produzir UM pedido de recalculo, nao
    /// dois - e o segundo custaria uma linha a mais em account_scores com
    /// exatamente os mesmos fatos.
    /// </summary>
    public static string ForScoreRequested(Guid accountId, DateTimeOffset requestedAt) =>
        $"score-requested:{accountId:N}:{requestedAt:yyyyMMddHHmm}";

    /// <summary>
    /// Chave do Product Matcher.
    ///
    /// Ancorada na SAFRA de score, e nao no tempo: o fit so muda quando os fatos
    /// mudam, e os fatos que ele le sao os mesmos que produziram aquele score.
    /// Recalcular o fit sobre a mesma safra gastaria uma chamada de modelo para
    /// reescrever o mesmo argumento.
    /// </summary>
    public static string ForMatch(Guid accountId, Guid accountScoreId) =>
        $"match:{accountId:N}:{accountScoreId:N}";

    public static string ForProductsMatched(Guid accountId, Guid accountScoreId) =>
        $"products-matched:{accountId:N}:{accountScoreId:N}";

    /// <summary>
    /// Chave do People Finder, ancorada na safra de fit: as personas que a busca
    /// persegue saem do pitch, e um fit novo pode mudar quem procurar.
    /// </summary>
    public static string ForContacts(Guid accountId, Guid productFitBatchId) =>
        $"contacts:{accountId:N}:{productFitBatchId:N}";

    public static string ForContactsFound(Guid accountId, Guid researchRunId) =>
        $"contacts-found:{accountId:N}:{researchRunId:N}";

    /// <summary>
    /// Conta pronta para abordagem. Uma vez por conta: <c>account.ready</c> e
    /// mudanca de estado, e nao safra. Se a conta voltar para pesquisa e ficar
    /// pronta de novo, quem re-emite e a transicao, com outra chave - e ate o
    /// SDR existir, um evento por conta e o que evita a fila encher de avisos
    /// repetidos que ninguem consome.
    /// </summary>
    public static string ForAccountReady(Guid accountId) =>
        $"account-ready:{accountId:N}";

    /// <summary>Formato do exemplo da secao 25: <c>email:{contact}:{sequence}:{step}</c>.</summary>
    public static string ForOutreach(Guid contactId, string sequenceName, int step, string channel) =>
        $"{channel}:{contactId:N}:{sequenceName}:{step}";
}
