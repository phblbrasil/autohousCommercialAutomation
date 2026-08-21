using System.Text;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.ReceitaFederal;

namespace AutoHous.Revenue.Ingestor;

/// <summary>
/// Le um arquivo delimitado de empresas.
///
/// Adaptador de entrada, nao caso de uso: ele so traduz linhas de texto em
/// <see cref="RawCompanyRow"/>. Nao valida CNPJ, nao filtra CNAE, nao decide
/// nada — quem faz isso e o dominio, depois que a linha ja esta gravada.
/// </summary>
public sealed class DelimitedCompanyReader
{
    /// <summary>
    /// Nomes de coluna aceitos por campo. Extratos da base da Receita circulam
    /// com cabecalhos diferentes conforme a ferramenta que os gerou; exigir um
    /// unico layout garantiria retrabalho manual a cada nova fonte.
    /// </summary>
    private static readonly Dictionary<string, string[]> ColumnAliases = new()
    {
        ["cnpj"] = ["cnpj", "cnpj_completo", "cnpj_basico", "num_cnpj", "documento"],
        ["razao_social"] = ["razao_social", "razaosocial", "razao", "nome_empresarial", "nome"],
        ["nome_fantasia"] = ["nome_fantasia", "nomefantasia", "fantasia", "nome_comercial"],
        ["cnae"] = ["cnae_principal", "cnae_fiscal", "cnae", "cnae_fiscal_principal", "atividade_principal"],
        ["situacao"] = ["situacao_cadastral", "situacao", "status", "descricao_situacao_cadastral"],
        ["municipio"] = ["municipio", "cidade", "nome_municipio"],
        ["uf"] = ["uf", "estado", "sigla_uf"]
    };

    public sealed record ReadResult(IReadOnlyList<RawCompanyRow> Rows, IReadOnlyList<string> UnmappedColumns);

    public async Task<ReadResult> ReadAsync(
        string path, char delimiter, Encoding encoding, CancellationToken ct = default)
    {
        using var reader = new StreamReader(path, encoding);

        var header = await reader.ReadLineAsync(ct)
            ?? throw new InvalidDataException($"Arquivo vazio: {path}");

        var columns = QuotedDelimitedLine.Split(header, delimiter)
            .Select((name, index) => (Key: Canonical(name), Index: index))
            .ToList();

        var map = new Dictionary<string, int>();

        foreach (var (field, aliases) in ColumnAliases)
        {
            // A ordem dos apelidos e significativa: o primeiro que casar vence.
            // "cnpj" antes de "cnpj_basico" evita mapear a raiz de oito digitos
            // quando o arquivo traz as duas colunas.
            var index = aliases
                .Select(alias => columns.FindIndex(c => c.Key == alias))
                .FirstOrDefault(i => i >= 0, -1);

            if (index >= 0) map[field] = index;
        }

        if (!map.ContainsKey("cnpj"))
        {
            throw new InvalidDataException(
                $"Nenhuma coluna de CNPJ reconhecida em '{path}'. " +
                $"Cabecalho lido: {string.Join(", ", columns.Select(c => c.Key))}");
        }

        var unmapped = columns
            .Where(c => !ColumnAliases.Values.Any(a => a.Contains(c.Key, StringComparer.Ordinal)))
            .Select(c => c.Key)
            .ToList();

        var rows = new List<RawCompanyRow>();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = QuotedDelimitedLine.Split(line, delimiter);

            rows.Add(new RawCompanyRow
            {
                Cnpj = Value(fields, map, "cnpj"),
                RazaoSocial = Value(fields, map, "razao_social"),
                NomeFantasia = Value(fields, map, "nome_fantasia"),
                CnaePrincipal = Value(fields, map, "cnae"),
                SituacaoCadastral = Value(fields, map, "situacao"),
                Municipio = Value(fields, map, "municipio"),
                Uf = Value(fields, map, "uf")
            });
        }

        return new ReadResult(rows, unmapped);
    }

    private static string? Value(string[] fields, Dictionary<string, int> map, string field) =>
        map.TryGetValue(field, out var index) && index < fields.Length && fields[index].Length > 0
            ? fields[index]
            : null;

    /// <summary>
    /// Cabecalhos vem com acento, espaco e maiuscula em combinacoes imprevisiveis.
    /// </summary>
    private static string Canonical(string name)
    {
        var trimmed = name.Trim().Trim('"').Trim();
        var normalized = NormalizeAscii(trimmed).ToLowerInvariant();

        return new string([.. normalized.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')])
            .Trim('_')
            .Replace("__", "_");
    }

    private static string NormalizeAscii(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
