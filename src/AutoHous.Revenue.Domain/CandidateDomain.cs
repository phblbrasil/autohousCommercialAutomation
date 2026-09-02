namespace AutoHous.Revenue.Domain;

/// <summary>
/// Deriva o domínio provável do site a partir do e-mail que a Receita registrou.
///
/// Existe porque **nenhuma das contas do ICP tem domínio**: a Receita não traz
/// site, e quem descobre é o Researcher — uma chamada de modelo por conta. O
/// e-mail, por outro lado, vem de graça para 41.518 estabelecimentos de venda de
/// veículos, e em empresa deste porte o domínio do e-mail costuma ser o site.
///
/// É uma **suposição**, não um fato, e o chamador precisa tratá-la assim. Por
/// isso o resultado é gravado em <c>probe_samples</c> com a origem declarada, e
/// nunca em <c>accounts.domain</c>: uma coisa é medir o mercado sobre um palpite;
/// outra é afirmar a uma abordagem comercial que aquele é o site do cliente.
/// </summary>
public static class CandidateDomain
{
    /// <summary>
    /// Provedores que hospedam e-mail mas não são o site de ninguém. Reusa a
    /// lista do <see cref="ContactPolicy"/> em vez de manter uma segunda: são a
    /// mesma pergunta — "este domínio pertence à empresa?" — e duas listas
    /// divergem no dia em que só uma for atualizada.
    /// </summary>
    private static readonly string[] NotWebsites =
    [
        .. ContactPolicy.PersonalEmailProviders,

        // Provedores de hospedagem e revenda comuns no setor: o e-mail é deles,
        // o site do cliente não.
        "locaweb.com.br", "uolhost.com.br", "hostgator.com.br", "kinghost.com.br",
        "registro.br", "webmail.com.br", "brturbo.com.br", "click21.com.br",
        "yahoo.com.br", "aol.com", "gmail.com.br"
    ];

    /// <summary>
    /// Devolve o domínio, ou <c>null</c> quando o e-mail não sustenta um palpite.
    /// </summary>
    public static string? FromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        // A Receita separa múltiplos endereços por vírgula ou ponto e vírgula em
        // parte das linhas. O primeiro é o que a empresa declarou primeiro.
        var first = email.Split([',', ';'], StringSplitOptions.TrimEntries)[0];

        var at = first.LastIndexOf('@');

        if (at <= 0 || at == first.Length - 1) return null;

        var domain = first[(at + 1)..].Trim().ToLowerInvariant().TrimEnd('.');

        // Um domínio precisa de ao menos um ponto e nada de espaço. A base tem
        // linha com e-mail truncado e com texto no lugar do endereço.
        if (domain.Length < 4 || !domain.Contains('.') || domain.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (domain.StartsWith("www.", StringComparison.Ordinal)) domain = domain[4..];

        return NotWebsites.Contains(domain, StringComparer.OrdinalIgnoreCase) ? null : domain;
    }

    /// <summary>URL absoluta para a sonda. HTTPS primeiro — redirect resolve o resto.</summary>
    public static string ToUrl(string domain) => $"https://{domain}";
}
