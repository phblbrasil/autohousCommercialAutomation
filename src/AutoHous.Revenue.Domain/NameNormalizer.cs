using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoHous.Revenue.Domain;

/// <summary>
/// Normaliza razao social, nome fantasia e nome de pessoa.
///
/// Usado hoje para a unicidade de <c>contacts</c>; sera a base das features de
/// similaridade do account graph (secao 11) no Sprint 1.
/// </summary>
public static partial class NameNormalizer
{
    /// <summary>
    /// Sufixos societarios. Publico porque a normalizacao para casamento e a
    /// montagem do nome de exibicao precisam da MESMA lista: duas listas que
    /// divergem produzem uma conta chamada "Vento Sul Ltda" cujo nome
    /// normalizado e "VENTO SUL" - e a divergencia so aparece quando o
    /// agrupamento erra.
    /// </summary>
    public static readonly string[] LegalSuffixes =
    [
        "LTDA", "LIMITADA", "SA", "S A", "EIRELI", "ME", "EPP", "MEI",
        "SOCIEDADE ANONIMA", "COMPANHIA", "CIA", "EMPRESA INDIVIDUAL"
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = RemoveDiacritics(value).ToUpperInvariant();

        // Pontuacao vira espaco para que "S.A." e "S/A" caiam no mesmo token.
        text = NonAlphanumeric().Replace(text, " ");
        text = Whitespace().Replace(text, " ").Trim();

        // Sufixos societarios sao removidos apenas no fim: "LTDA MOTORES" e um
        // nome legitimo e nao pode perder o primeiro token.
        bool removed;
        do
        {
            removed = false;
            foreach (var suffix in LegalSuffixes)
            {
                if (text.Length > suffix.Length && text.EndsWith(" " + suffix, StringComparison.Ordinal))
                {
                    text = text[..^(suffix.Length + 1)].Trim();
                    removed = true;
                }
            }
        }
        while (removed);

        return text;
    }

    /// <summary>
    /// Remove o sufixo societario preservando a caixa original. Usado no nome de
    /// exibicao: "Comercio de Veiculos da Serra Ltda" vira "Comercio de Veiculos
    /// da Serra", enquanto <c>razao_social</c> guarda a forma legal completa.
    /// </summary>
    public static string StripLegalSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Trim();

        bool removed;
        do
        {
            removed = false;

            foreach (var suffix in LegalSuffixes)
            {
                var candidate = " " + suffix;

                if (text.Length > candidate.Length &&
                    text.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^candidate.Length].TrimEnd();
                    removed = true;
                }
            }
        }
        while (removed);

        return text;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"[^A-Z0-9]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
