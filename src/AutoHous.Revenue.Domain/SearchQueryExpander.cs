namespace AutoHous.Revenue.Domain;

/// <summary>
/// Expande a consulta do usuario com sinonimos do dominio antes de virar tsquery.
///
/// Existe por uma limitacao concreta do stemmer portugues (Snowball): substantivo
/// e verbo da mesma familia produzem stems DIFERENTES.
///
///   expansão   -> 'expansa'
///   expandindo -> 'expand'
///
/// Quem busca "expansao" nao encontraria "o grupo esta expandindo" - justamente o
/// sinal comercial que mais interessa. Plurais o stemmer resolve sozinho
/// (lojas -> loj), entao a lista abaixo cobre so as familias substantivo/verbo.
///
/// A alternativa canonica seria um dicionario de sinonimos do proprio Postgres,
/// mas ele exige arquivo em $SHAREDIR/tsearch_data no servidor - inviavel em
/// Postgres gerenciado como o Supabase. Expandir na aplicacao e portavel.
/// </summary>
public static class SearchQueryExpander
{
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["expansao"]      = ["expandir", "expandindo", "crescimento"],
        ["expansão"]      = ["expandir", "expandindo", "crescimento"],
        ["inauguracao"]   = ["inaugurar", "inaugurada", "abertura"],
        ["inauguração"]   = ["inaugurar", "inaugurada", "abertura"],
        ["contratacao"]   = ["contratar", "contratando", "vaga"],
        ["contratação"]   = ["contratar", "contratando", "vaga"],
        ["investimento"]  = ["investir", "investindo"],
        ["aquisicao"]     = ["adquirir", "comprou", "fusao"],
        ["aquisição"]     = ["adquirir", "comprou", "fusao"],
        ["migracao"]      = ["migrar", "migrando", "troca"],
        ["migração"]      = ["migrar", "migrando", "troca"],
        ["concessionaria"] = ["concessionarias", "revenda"],
        ["concessionária"] = ["concessionarias", "revenda"],
        ["estoque"]       = ["inventario", "vitrine"],
        ["seminovos"]     = ["usados", "seminovo"]
    };

    /// <summary>
    /// Acrescenta os sinonimos como alternativas OR. O formato produzido continua
    /// valido para <c>websearch_to_tsquery</c>, que entende OR, aspas e exclusao
    /// com "-".
    /// </summary>
    public static string Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        var trimmed = query.Trim();

        // Consulta entre aspas e busca por frase exata: expandir mudaria a intencao.
        if (trimmed.Contains('"')) return trimmed;

        var additions = new List<string>();

        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Termo de exclusao nao deve puxar sinonimos junto.
            if (token.StartsWith('-')) continue;

            if (Synonyms.TryGetValue(token, out var synonyms))
            {
                additions.AddRange(synonyms.Where(s =>
                    !trimmed.Contains(s, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return additions.Count == 0
            ? trimmed
            : $"{trimmed} OR {string.Join(" OR ", additions.Distinct())}";
    }
}
