namespace AutoHous.Revenue.Domain;

/// <summary>
/// Maquina de estados da account. Materializa o ADR-002: o agente pode sugerir
/// um proximo estado, mas quem autoriza a transicao e a plataforma.
///
/// Cobre apenas o ciclo de vida da ACCOUNT. Os estagios de negociacao
/// (meeting -> won/lost) pertencem a <c>opportunities</c> e formam uma segunda
/// maquina, acoplada a esta por evento.
/// </summary>
public static class AccountStatusTransitions
{
    private static readonly Dictionary<AccountStatus, AccountStatus[]> Allowed = new()
    {
        [AccountStatus.Discovered] =
        [
            AccountStatus.Researching, AccountStatus.Suppressed, AccountStatus.Rejected
        ],

        // Volta para Discovered quando a pesquisa falha e sera reenfileirada.
        [AccountStatus.Researching] =
        [
            AccountStatus.Researched, AccountStatus.Discovered, AccountStatus.Suppressed
        ],

        [AccountStatus.Researched] =
        [
            AccountStatus.Scored, AccountStatus.Researching, AccountStatus.Nurture,
            AccountStatus.Suppressed, AccountStatus.Rejected
        ],

        [AccountStatus.Scored] =
        [
            AccountStatus.Ready, AccountStatus.Nurture, AccountStatus.Researching,
            AccountStatus.Suppressed, AccountStatus.Rejected
        ],

        [AccountStatus.Ready] =
        [
            AccountStatus.Contacted, AccountStatus.Nurture, AccountStatus.Suppressed
        ],

        [AccountStatus.Contacted] =
        [
            AccountStatus.Engaged, AccountStatus.Nurture, AccountStatus.Suppressed
        ],

        [AccountStatus.Engaged] =
        [
            AccountStatus.Customer, AccountStatus.Nurture, AccountStatus.Suppressed
        ],

        [AccountStatus.Nurture] =
        [
            AccountStatus.Ready, AccountStatus.Researching, AccountStatus.Suppressed,
            AccountStatus.Rejected
        ],

        // Regra dura da secao 18: uma vez cliente, nunca mais cold outbound.
        [AccountStatus.Customer] = [AccountStatus.Suppressed],

        // Terminais na maquina automatica. Reverter exige acao humana explicita,
        // fora deste caminho de codigo.
        [AccountStatus.Suppressed] = [],
        [AccountStatus.Rejected] = []
    };

    public static bool CanTransition(AccountStatus from, AccountStatus to) =>
        from == to || (Allowed.TryGetValue(from, out var targets) && targets.Contains(to));

    /// <summary>
    /// Valida a transicao ou lanca. Usar sempre antes de escrever
    /// <c>accounts.status</c> - nenhum handler deve alterar o estado direto.
    /// </summary>
    public static void EnsureCanTransition(AccountStatus from, AccountStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidAccountTransitionException(from, to);
        }
    }

    public static IReadOnlyCollection<AccountStatus> AllowedFrom(AccountStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];

    /// <summary>Estados em que a account nunca pode receber outbound (secao 18).</summary>
    public static bool BlocksOutbound(AccountStatus status) =>
        status is AccountStatus.Suppressed or AccountStatus.Customer or AccountStatus.Rejected;
}

public sealed class InvalidAccountTransitionException(AccountStatus from, AccountStatus to)
    : InvalidOperationException($"Transicao de account invalida: {from} -> {to}.")
{
    public AccountStatus From { get; } = from;
    public AccountStatus To { get; } = to;
}
