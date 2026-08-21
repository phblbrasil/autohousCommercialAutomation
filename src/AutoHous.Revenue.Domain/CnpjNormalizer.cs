namespace AutoHous.Revenue.Domain;

/// <summary>
/// Normaliza e valida CNPJ. Rejeitar na borda importa: um CNPJ invalido que vira
/// account fantasma consome pesquisa paga de IA antes de alguem perceber.
/// </summary>
public static class CnpjNormalizer
{
    private static readonly int[] FirstWeights  = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] SecondWeights = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    /// <summary>Remove mascara e retorna 14 digitos, sem validar.</summary>
    public static string StripMask(string? cnpj) =>
        string.IsNullOrWhiteSpace(cnpj)
            ? string.Empty
            : new string(cnpj.Where(char.IsAsciiDigit).ToArray());

    public static bool IsValid(string? cnpj)
    {
        var digits = StripMask(cnpj);

        if (digits.Length != 14) return false;

        // 00000000000000, 11111111111111 etc. passam no calculo mas nao existem.
        if (digits.Distinct().Count() == 1) return false;

        return CheckDigit(digits, FirstWeights) == digits[12] - '0'
            && CheckDigit(digits, SecondWeights) == digits[13] - '0';
    }

    /// <summary>Normaliza para o formato de <c>companies_cnpj.cnpj</c> (char(14)).</summary>
    public static string Normalize(string? cnpj)
    {
        if (!IsValid(cnpj)) throw new ArgumentException($"CNPJ invalido: '{cnpj}'.", nameof(cnpj));
        return StripMask(cnpj);
    }

    public static bool TryNormalize(string? cnpj, out string normalized)
    {
        if (IsValid(cnpj))
        {
            normalized = StripMask(cnpj);
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static string Format(string cnpj)
    {
        var d = StripMask(cnpj);
        return d.Length != 14 ? cnpj : $"{d[..2]}.{d[2..5]}.{d[5..8]}/{d[8..12]}-{d[12..]}";
    }

    private static int CheckDigit(string digits, int[] weights)
    {
        var sum = weights.Select((w, i) => (digits[i] - '0') * w).Sum();
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
