using System.Text;

namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>
/// Divisao de uma linha delimitada, respeitando aspas.
///
/// Existe como tipo proprio porque duas fontes precisam exatamente da mesma
/// regra: os arquivos da Receita e o leitor de CSV do Ingestor. Razao social com
/// o delimitador dentro e comum ("COMERCIO DE VEICULOS SILVA, SANTOS E CIA
/// LTDA"), e duas implementacoes da mesma divisao divergiriam no primeiro caso
/// estranho.
/// </summary>
public static class QuotedDelimitedLine
{
    public static string[] Split(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                // Aspas duplicadas dentro de campo entre aspas sao um literal.
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return [.. fields];
    }

    /// <summary>
    /// Campo pela posicao. Devolve <c>null</c> para campo ausente ou vazio: a
    /// Receita usa string vazia como "nao informado" em quase toda coluna
    /// opcional, e propagar <c>""</c> ate o banco encheria as colunas de vazio
    /// que nao e nulo e nao e valor.
    /// </summary>
    public static string? At(string[] fields, int index) =>
        index >= 0 && index < fields.Length && fields[index].Length > 0
            ? fields[index]
            : null;
}
