using System.Text;
using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Monta o prompt do People Finder a partir do arquivo versionado, do schema, do
/// contexto da conta e do BRIEFING de busca.
///
/// O briefing carrega as personas a procurar, e e o que impede o agente de
/// devolver o organograma inteiro. Vinte pessoas custam vinte evidencias, vinte
/// validacoes e vinte linhas de PII no banco para aproveitar duas.
///
/// O prompt repete os pisos de confianca em numeros, e nao so no texto do
/// arquivo versionado. Duplicacao consciente: sao os dois valores que mais
/// rejeitam run neste agente, e o custo de uma rejeicao aqui e uma busca inteira
/// refeita. As constantes saem do <see cref="ContactPolicy"/>, entao mudar a
/// politica muda o prompt junto - nao ha um numero escrito a mao que possa
/// divergir do que a plataforma verifica.
/// </summary>
public sealed class PeopleFinderPromptBuilder : IPeopleFinderPromptBuilder
{
    public const string PromptVersionValue = "people-finder-v1";
    public const string AgentNameValue = "people-finder";

    public string PromptVersion => PromptVersionValue;
    public string AgentName => AgentNameValue;

    private readonly string _template;
    private readonly string _schema;

    public PeopleFinderPromptBuilder(string promptPath, string schemaPath)
    {
        if (!File.Exists(promptPath))
            throw new FileNotFoundException($"Prompt do People Finder nao encontrado: {promptPath}");

        if (!File.Exists(schemaPath))
            throw new FileNotFoundException($"Schema do Contact Discovery nao encontrado: {schemaPath}");

        _template = File.ReadAllText(promptPath);
        _schema = File.ReadAllText(schemaPath);
    }

    public string BuildSystemPrompt() =>
        _template
            .Replace("{{OUTPUT_SCHEMA}}", _schema)
            .Replace("{{ACCOUNT_CONTEXT}}", "(fornecido na mensagem do usuario)");

    public string BuildUserPrompt(AccountContext context, PeopleSearchBrief brief) =>
        $"""
        Descubra quem decide nesta empresa e devolva o Contact Discovery em JSON.

        ## Conta

        {Serialize(context)}

        ## O que procurar

        {Describe(brief)}

        ## Pisos que a plataforma verifica

        {Thresholds()}
        """;

    public string BuildRepairPrompt(
        AccountContext context, PeopleSearchBrief brief, string previousOutput, string violations) =>
        $"""
        A resposta anterior nao satisfez o contrato e foi rejeitada.

        Violacoes encontradas:
        {violations}

        Corrija e devolva APENAS o objeto JSON valido, sem texto ao redor.

        Nao invente para preencher: se um contato ou canal nao alcanca o piso de
        confianca, REMOVA-O. Uma lista menor e valida e melhor que uma completa e
        recusada - e muito melhor que uma completa e aceita com dado inventado.

        Resposta anterior:
        {Truncate(previousOutput)}

        ## Conta

        {Serialize(context)}

        ## O que procurar

        {Describe(brief)}

        ## Pisos que a plataforma verifica

        {Thresholds()}
        """;

    internal static string Describe(PeopleSearchBrief brief)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(brief.EntryProduct))
        {
            sb.AppendLine($"Produto de entrada .. {brief.EntryProduct}");
        }

        sb.AppendLine("Cargos a procurar, em ordem de prioridade:");

        for (var i = 0; i < brief.Personas.Count; i++)
        {
            sb.AppendLine($"  {i + 1}. {brief.Personas[i]}");
        }

        sb.AppendLine();
        sb.AppendLine(
            "Registre em `searched_without_result` os que voce procurou e nao achou. " +
            "Nao encontrar e um resultado: significa que a funcao e acumulada por outra pessoa.");

        if (!string.IsNullOrWhiteSpace(brief.Angle))
        {
            sb.AppendLine();
            sb.AppendLine("Angulo da conversa que estas pessoas vao receber:");
            sb.AppendLine($"  \"{brief.Angle}\"");
            sb.AppendLine();
            sb.AppendLine(
                "Nao reescreva o angulo, e nao o cite na sua resposta. Ele esta aqui apenas " +
                "para voce julgar quem tem alcada sobre esse assunto nesta empresa.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Os numeros saem das constantes do dominio: a politica e o prompt nao
    /// podem divergir, e a unica forma de garantir isso e nao escrever o numero
    /// duas vezes.
    /// </summary>
    private static string Thresholds() =>
        $"""
        - Confianca do CONTATO ..... minimo {ContactPolicy.MinimumContactConfidence:0.00}
        - Confianca do CANAL ....... minimo {ContactPolicy.MinimumChannelConfidence:0.00}
        - Para `email`, `mobile` e `whatsapp`, o `evidence_index` do canal precisa ser
          DIFERENTE do `evidence_index` do contato. Aponte para a pagina em que o CANAL
          aparece. Endereco deduzido do padrao da empresa nao tem lastro e o run e recusado.
        - Abaixo de qualquer piso: OMITA o item. A plataforma rejeita o run inteiro em vez
          de descartar a linha em silencio.
        """;

    private static string Serialize(AccountContext context) =>
        JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });

    private static string Truncate(string text, int max = 6000) =>
        text.Length <= max ? text : text[..max] + "\n...(truncado)";
}
