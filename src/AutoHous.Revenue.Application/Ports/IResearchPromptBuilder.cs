namespace AutoHous.Revenue.Application;

/// <summary>
/// Monta os prompts do Researcher. Porta porque o caso de uso precisa do texto,
/// nao de saber que ele vem de um arquivo versionado em disco.
/// </summary>
public interface IResearchPromptBuilder
{
    string AgentName { get; }
    string PromptVersion { get; }

    string BuildSystemPrompt();
    string BuildUserPrompt(AccountContext context);

    /// <summary>Devolve ao agente exatamente o que violou o contrato.</summary>
    string BuildRepairPrompt(AccountContext context, string previousOutput, string violations);
}
