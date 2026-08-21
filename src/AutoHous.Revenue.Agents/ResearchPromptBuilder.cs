using System.Text.Json;
using AutoHous.Revenue.Application;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Monta o prompt do Researcher a partir do arquivo versionado, do schema e do
/// contexto da conta.
///
/// O prompt vem de disco e nao de string embutida porque agent_runs.prompt_version
/// so tem valor se a versao for auditavel: e ela que permite reprocessar e
/// comparar safras (ADR-004 aplicado a prompts).
/// </summary>
public sealed class ResearchPromptBuilder : IResearchPromptBuilder
{
    public const string PromptVersionValue = "researcher-v1";
    public const string AgentNameValue = "researcher";

    public string PromptVersion => PromptVersionValue;
    public string AgentName => AgentNameValue;

    private readonly string _template;
    private readonly string _schema;

    public ResearchPromptBuilder(string promptPath, string schemaPath)
    {
        if (!File.Exists(promptPath))
            throw new FileNotFoundException($"Prompt do Researcher nao encontrado: {promptPath}");

        if (!File.Exists(schemaPath))
            throw new FileNotFoundException($"Schema do Research Profile nao encontrado: {schemaPath}");

        _template = File.ReadAllText(promptPath);
        _schema = File.ReadAllText(schemaPath);
    }

    public string BuildSystemPrompt() =>
        _template
            .Replace("{{OUTPUT_SCHEMA}}", _schema)
            .Replace("{{ACCOUNT_CONTEXT}}", "(fornecido na mensagem do usuario)");

    public string BuildUserPrompt(AccountContext context) =>
        $"""
        Pesquise esta conta e devolva o Research Profile em JSON.

        {JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true })}
        """;

    /// <summary>
    /// Prompt de reparo: devolve ao agente exatamente o que violou o contrato.
    /// Uma unica tentativa - se o modelo nao acerta com os erros em maos, o
    /// problema e do prompt e insistir so queima orcamento.
    /// </summary>
    public string BuildRepairPrompt(AccountContext context, string previousOutput, string violations) =>
        $"""
        A resposta anterior nao satisfez o contrato e foi rejeitada.

        Violacoes encontradas:
        {violations}

        Corrija e devolva APENAS o objeto JSON valido, sem texto ao redor.

        Resposta anterior:
        {Truncate(previousOutput)}

        Conta:
        {JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true })}
        """;

    private static string Truncate(string text, int max = 6000) =>
        text.Length <= max ? text : text[..max] + "\n...(truncado)";
}
