using System.Text;
using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Monta o prompt do Product Matcher a partir do arquivo versionado, do schema,
/// do contexto da conta e do DIAGNOSTICO ja calculado.
///
/// O diagnostico entra como quadro em texto, e nao como JSON cru, pela mesma
/// razao que a medicao da sonda entra assim no auditor - e por uma a mais.
///
/// A razao compartilhada: um objeto com cinco produtos, cada um com cinco
/// criterios de cinco campos, e ruido que o modelo le mal. A razao especifica
/// desta etapa: o campo <c>Observed</c>. Em JSON, <c>"observed": false</c> ao
/// lado de <c>"points": 0</c> convida a leitura "esse criterio vale zero", e o
/// agente escreve confiante que a vitrine da empresa e ruim quando ninguem a
/// auditou. O quadro escreve <c>[nao observado]</c> por extenso, na frente da
/// linha, que e o unico jeito de a distincao sobreviver a leitura.
/// </summary>
public sealed class ProductPitchPromptBuilder : IProductPitchPromptBuilder
{
    public const string PromptVersionValue = "product-matcher-v1";
    public const string AgentNameValue = "product-matcher";

    public string PromptVersion => PromptVersionValue;
    public string AgentName => AgentNameValue;

    private readonly string _template;
    private readonly string _schema;

    public ProductPitchPromptBuilder(string promptPath, string schemaPath)
    {
        if (!File.Exists(promptPath))
            throw new FileNotFoundException($"Prompt do Product Matcher nao encontrado: {promptPath}");

        if (!File.Exists(schemaPath))
            throw new FileNotFoundException($"Schema do Product Pitch nao encontrado: {schemaPath}");

        _template = File.ReadAllText(promptPath);
        _schema = File.ReadAllText(schemaPath);
    }

    public string BuildSystemPrompt() =>
        _template
            .Replace("{{OUTPUT_SCHEMA}}", _schema)
            .Replace("{{ACCOUNT_CONTEXT}}", "(fornecido na mensagem do usuario)");

    public string BuildUserPrompt(AccountContext context, IReadOnlyList<ProductFit> fits) =>
        $"""
        Escreva o argumento comercial para esta conta e devolva o Product Pitch em JSON.

        ## Conta

        {Serialize(context)}

        ## Diagnostico ja calculado pela plataforma

        Estas notas ESTAO DECIDIDAS. Nao as recalcule, nao discorde e nao reordene.
        Escreva um pitch para cada produto abaixo, e apenas para eles.

        {Describe(fits)}

        ## Personas disponiveis por produto

        Voce pode restringir estas listas. Nao pode acrescentar cargo fora delas.

        {Personas(fits)}
        """;

    public string BuildRepairPrompt(
        AccountContext context, IReadOnlyList<ProductFit> fits, string previousOutput, string violations) =>
        $"""
        A resposta anterior nao satisfez o contrato e foi rejeitada.

        Violacoes encontradas:
        {violations}

        Corrija e devolva APENAS o objeto JSON valido, sem texto ao redor.

        Resposta anterior:
        {Truncate(previousOutput)}

        ## Conta

        {Serialize(context)}

        ## Diagnostico ja calculado pela plataforma

        {Describe(fits)}

        ## Personas disponiveis por produto

        {Personas(fits)}
        """;

    /// <summary>
    /// Quadro do diagnostico. Cada criterio vira uma linha com os pontos, o
    /// maximo e a justificativa - e criterio nao observado leva o rotulo na
    /// frente, antes de qualquer numero.
    /// </summary>
    internal static string Describe(IReadOnlyList<ProductFit> fits)
    {
        var sb = new StringBuilder();

        foreach (var fit in fits)
        {
            sb.AppendLine($"### {fit.Product} — {fit.Score:0}/100" +
                          (fit.RecommendedEntry ? "  ← PORTA DE ENTRADA" : string.Empty));
            sb.AppendLine($"cobertura do diagnostico: {fit.Coverage:P0}");
            sb.AppendLine();

            foreach (var reason in fit.Reasons)
            {
                var flag = reason.Observed ? "  " : "[nao observado] ";

                sb.AppendLine($"  {flag}{reason.Criterion} .... {reason.Points:0}/{reason.MaxPoints:0} — {reason.Rationale}");
            }

            var unobserved = fit.Reasons.Where(r => !r.Observed).ToList();

            if (unobserved.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    $"  ATENCAO: {unobserved.Count} criterio(s) NAO OBSERVADO(S). " +
                    "Zero pontos ali significa que ninguem olhou, e nao que esta bom. " +
                    "Nao construa o angulo sobre nenhum deles.");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string Personas(IReadOnlyList<ProductFit> fits)
    {
        var sb = new StringBuilder();

        foreach (var fit in fits)
        {
            sb.AppendLine($"- {fit.Product}: {string.Join(", ", fit.Personas)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Serialize(AccountContext context) =>
        JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });

    private static string Truncate(string text, int max = 6000) =>
        text.Length <= max ? text : text[..max] + "\n...(truncado)";
}
