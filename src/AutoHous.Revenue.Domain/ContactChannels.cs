using System.Text.RegularExpressions;

namespace AutoHous.Revenue.Domain;

/// <summary>Canais aceitos em <c>contact_channels.channel</c>.</summary>
public static class ContactChannel
{
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Mobile = "mobile";
    public const string Whatsapp = "whatsapp";
    public const string LinkedIn = "linkedin";

    public static readonly string[] All = [Email, Phone, Mobile, Whatsapp, LinkedIn];

    public static bool IsKnown(string channel) =>
        All.Contains(channel, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Normaliza o valor de um canal para <c>contact_channels.normalized_value</c>.
///
/// A coluna existe por causa do indice <c>unique(contact_id, channel,
/// normalized_value)</c>: sem normalizacao, "(51) 99123-4567", "51991234567" e
/// "+55 51 99123 4567" sao tres linhas distintas para o mesmo telefone, e o
/// People Finder rodando duas vezes triplica a agenda em vez de confirmar o que
/// ja sabia.
///
/// Devolve <c>null</c> para o que nao consegue normalizar. Nulo escapa do indice
/// unico parcial de proposito: e melhor guardar um valor estranho sem dedupe do
/// que descartar um contato porque o formato surpreendeu o normalizador.
/// </summary>
public static partial class ContactChannelNormalizer
{
    public static string? Normalize(string channel, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();

        return channel.ToLowerInvariant() switch
        {
            ContactChannel.Email => NormalizeEmail(trimmed),
            ContactChannel.Phone or ContactChannel.Mobile or ContactChannel.Whatsapp => NormalizePhone(trimmed),
            ContactChannel.LinkedIn => NormalizeLinkedIn(trimmed),
            _ => null
        };
    }

    private static string? NormalizeEmail(string value)
    {
        var lowered = value.ToLowerInvariant();

        // Sem validacao de formato completa: o schema ja exige "format": "email",
        // e duplicar a regra aqui criaria duas definicoes de e-mail valido que um
        // dia divergem.
        return lowered.Contains('@') ? lowered : null;
    }

    /// <summary>
    /// Telefone brasileiro em E.164 sem o "+": <c>5551991234567</c>.
    ///
    /// O nono digito e o problema real. Numeros de celular publicados antes de
    /// 2016 aparecem com oito digitos no corpo, e a mesma pessoa com e sem o
    /// nono digito seriam dois contatos. Aqui o nono e ACRESCENTADO quando o
    /// numero tem cara de celular - primeiro digito 6 a 9 no corpo de oito.
    /// </summary>
    private static string? NormalizePhone(string value)
    {
        var digits = NonDigits().Replace(value, string.Empty);

        if (digits.Length == 0) return null;

        // Prefixo internacional em duas formas: "+55..." vira "55...", e um "00"
        // de discagem internacional idem.
        if (digits.StartsWith("00", StringComparison.Ordinal) && digits.Length > 4)
        {
            digits = digits[2..];
        }

        var hasCountry = digits.Length is 12 or 13 && digits.StartsWith("55", StringComparison.Ordinal);
        var national = hasCountry ? digits[2..] : digits;

        // Sem DDD nao da para deduplicar com seguranca: "991234567" pode ser de
        // qualquer estado, e assumir um significa fundir contatos de empresas
        // diferentes.
        if (national.Length is not (10 or 11)) return null;

        var ddd = national[..2];
        var body = national[2..];

        if (body.Length == 8 && body[0] >= '6')
        {
            body = "9" + body;
        }

        return $"55{ddd}{body}";
    }

    /// <summary>
    /// Reduz ao identificador do perfil: <c>linkedin.com/in/fulano-silva</c>.
    /// Query string de campanha e <c>www</c> nao fazem parte da identidade.
    /// </summary>
    private static string? NormalizeLinkedIn(string value)
    {
        var match = LinkedInProfile().Match(value);

        if (!match.Success) return null;

        var slug = match.Groups["slug"].Value.Trim('/').ToLowerInvariant();

        return slug.Length == 0 ? null : $"linkedin.com/in/{slug}";
    }

    [GeneratedRegex(@"[^\d]")] private static partial Regex NonDigits();
    [GeneratedRegex(@"linkedin\.com/in/(?<slug>[^/?#]+)", RegexOptions.IgnoreCase)] private static partial Regex LinkedInProfile();
}

/// <summary>
/// A politica de PII do frame 09, no dominio.
///
/// Vive aqui e nao na camada de agentes pela mesma razao do
/// <see cref="EvidenceFirstGuard"/>: "que dado de pessoa fisica esta empresa
/// pode guardar" e regra de negocio, e escreve-la dentro do prompt a deixaria
/// valendo apenas enquanto o modelo cooperasse.
///
/// O criterio central e a distincao entre contato PROFISSIONAL e PESSOAL. Um
/// e-mail corporativo publicado no site institucional e dado de contato de
/// negocio; o Gmail pessoal do mesmo diretor, achado num cadastro qualquer, e
/// outra coisa - e a diferenca entre uma abordagem B2B legitima e uma que a LGPD
/// nao ampara.
/// </summary>
public static class ContactPolicy
{
    /// <summary>
    /// Provedores de e-mail pessoal. Nao e lista de bloqueio: um e-mail nestes
    /// dominios entra, marcado como pessoal, e simplesmente nao conta como
    /// "e-mail profissional" nos 5 pontos de contactability. Revendas pequenas
    /// operam de verdade com Gmail, e descartar o dado deixaria a conta sem
    /// nenhum contato.
    /// </summary>
    public static readonly string[] PersonalEmailProviders =
    [
        "gmail.com", "hotmail.com", "outlook.com", "outlook.com.br", "live.com",
        "yahoo.com", "yahoo.com.br", "bol.com.br", "uol.com.br", "terra.com.br",
        "ig.com.br", "globo.com", "icloud.com", "me.com", "protonmail.com", "zoho.com"
    ];

    /// <summary>
    /// Confianca minima para gravar um contato. Abaixo disto o achado nao vira
    /// linha: um nome com 30% de confianca custa mais caro errado - abordagem
    /// dirigida a pessoa que nao trabalha ali - do que ausente.
    /// </summary>
    public const decimal MinimumContactConfidence = 0.5m;

    /// <summary>
    /// Confianca minima para gravar um CANAL. Mais alta que a do contato: errar
    /// a pessoa e constrangedor, errar o canal manda a mensagem para um
    /// terceiro que nunca pediu para receber nada.
    /// </summary>
    public const decimal MinimumChannelConfidence = 0.6m;

    public static bool IsProfessionalEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        var at = email.LastIndexOf('@');

        if (at < 0 || at == email.Length - 1) return false;

        var domain = email[(at + 1)..].Trim().ToLowerInvariant();

        return !PersonalEmailProviders.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// O e-mail e do dominio da propria conta? E o lastro mais forte que existe
    /// para "esta pessoa trabalha aqui" sem depender do que o modelo afirmou.
    /// </summary>
    public static bool MatchesAccountDomain(string? email, string? accountDomain)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(accountDomain)) return false;

        var at = email.LastIndexOf('@');
        if (at < 0) return false;

        var emailDomain = email[(at + 1)..].Trim().ToLowerInvariant();

        var host = accountDomain.Trim().ToLowerInvariant()
            .Replace("https://", string.Empty)
            .Replace("http://", string.Empty)
            .Split('/')[0];

        // Prefixo literal, e nao TrimStart('w', '.'): aquele comeria o 'w' de
        // "webmotors.com.br" e o dominio viraria "ebmotors.com.br".
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        return emailDomain == host ||
               emailDomain.EndsWith("." + host, StringComparison.Ordinal);
    }
}
