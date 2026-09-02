using System.Text.Json;
using AngleSharp.Dom;

namespace AutoHous.Revenue.WebAudit;

/// <summary>
/// Lê os blocos <c>application/ld+json</c> do documento e responde as duas
/// perguntas que a auditoria faz: <b>que tipos</b> a página declara, e se ela
/// carrega <b>nome, endereço e telefone</b> juntos.
///
/// Os tipos importam porque "tem dado estruturado" era um booleano que não
/// distinguia um rodapé com <c>Organization</c> de uma vitrine inteira marcada
/// com <c>Vehicle</c> e <c>Offer</c> — e é a segunda que faz o estoque ser
/// citável por um motor de resposta.
///
/// O NAP importa porque é o mínimo para um motor afirmar QUAL negócio é este.
/// Sem ele a loja pode estar bem escrita e ainda assim ser indistinguível de
/// outra de nome parecido na mesma cidade.
///
/// JSON inválido é ignorado em silêncio de propósito: metade dos sites do setor
/// tem um bloco quebrado, e derrubar a auditoria inteira por causa dele trocaria
/// um diagnóstico parcial por nenhum.
/// </summary>
public static class JsonLd
{
    public sealed record Result(IReadOnlyList<string> Types, bool HasNap);

    private static readonly string[] NameKeys = ["name", "legalName"];
    private static readonly string[] PhoneKeys = ["telephone", "phone"];
    private static readonly string[] AddressKeys = ["address", "streetAddress"];

    public static Result Read(IDocument doc)
    {
        var types = new List<string>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var text = script.TextContent;

            if (string.IsNullOrWhiteSpace(text)) continue;

            try
            {
                using var json = JsonDocument.Parse(text);
                Walk(json.RootElement, types, found);
            }
            catch (JsonException)
            {
                // Bloco quebrado. Ver o resumo da classe.
            }
        }

        var hasNap = NameKeys.Any(found.Contains)
                     && PhoneKeys.Any(found.Contains)
                     && AddressKeys.Any(found.Contains);

        return new Result(
            [.. types.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)],
            hasNap);
    }

    /// <summary>
    /// Desce a árvore inteira. Não dá para olhar só a raiz: o padrão real do
    /// setor é um <c>@graph</c> com a loja, o endereço e cada veículo aninhados,
    /// e é justamente o <c>Vehicle</c> lá no fundo que interessa.
    /// </summary>
    private static void Walk(JsonElement element, List<string> types, HashSet<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    keys.Add(property.Name);

                    if (property.NameEquals("@type"))
                    {
                        AddTypes(property.Value, types);
                    }

                    Walk(property.Value, types, keys);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, types, keys);
                }
                break;
        }
    }

    /// <summary><c>@type</c> aceita string ou lista — a especificação permite os dois.</summary>
    private static void AddTypes(JsonElement value, List<string> types)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is { Length: > 0 } single) types.Add(single);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                {
                    types.Add(s);
                }
            }
        }
    }
}
