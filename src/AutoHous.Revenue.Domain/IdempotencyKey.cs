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

    /// <summary>Formato do exemplo da secao 25: <c>email:{contact}:{sequence}:{step}</c>.</summary>
    public static string ForOutreach(Guid contactId, string sequenceName, int step, string channel) =>
        $"{channel}:{contactId:N}:{sequenceName}:{step}";
}
