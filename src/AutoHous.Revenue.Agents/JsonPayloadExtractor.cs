using AutoHous.Revenue.Application;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Extrai o objeto JSON de uma resposta de LLM.
///
/// Existe porque modelos raramente devolvem JSON puro: cercam em bloco de codigo,
/// escrevem "Aqui esta o resultado:" antes, ou acrescentam comentario depois.
/// A documentacao oficial do Hermes confirma que skills nao possuem mecanismo de
/// structured output forcado - logo isto e caminho critico, nao conveniencia.
/// </summary>
public static class JsonPayloadExtractor
{
    public static bool TryExtract(string? rawText, out JsonNode? node, out string? error)
    {
        node = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawText))
        {
            error = "Resposta do agente vazia.";
            return false;
        }

        foreach (var candidate in Candidates(rawText))
        {
            try
            {
                var parsed = JsonNode.Parse(
                    candidate,
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                if (parsed is JsonObject)
                {
                    node = parsed;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Candidato invalido: segue para o proximo.
            }
        }

        error = "Nenhum objeto JSON valido encontrado na resposta do agente.";
        return false;
    }

    /// <summary>Candidatos do mais provavel ao mais tolerante.</summary>
    private static IEnumerable<string> Candidates(string text)
    {
        var trimmed = text.Trim();

        // 1. A resposta inteira ja e JSON.
        yield return trimmed;

        // 2. Blocos cercados ```json ... ``` (ou ``` ... ```).
        foreach (var fenced in FencedBlocks(trimmed))
        {
            yield return fenced;
        }

        // 3. Recorte entre a primeira '{' e a ultima '}' - resgata JSON cercado
        //    de prosa nos dois lados.
        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');

        if (first >= 0 && last > first)
        {
            yield return trimmed[first..(last + 1)];
        }
    }

    private static IEnumerable<string> FencedBlocks(string text)
    {
        var index = 0;

        while (true)
        {
            var open = text.IndexOf("```", index, StringComparison.Ordinal);
            if (open < 0) yield break;

            var lineEnd = text.IndexOf('\n', open);
            if (lineEnd < 0) yield break;

            var close = text.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (close < 0) yield break;

            yield return text[(lineEnd + 1)..close].Trim();
            index = close + 3;
        }
    }
}
