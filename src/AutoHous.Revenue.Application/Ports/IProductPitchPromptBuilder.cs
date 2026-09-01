using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Monta os prompts do Product Matcher (A04).
///
/// Porta propria pela mesma razao que o auditor tem a dele, e pelo mesmo tipo de
/// motivo: a assinatura carrega o que o agente NAO pode recalcular. Aqui e o
/// diagnostico ja apurado - cada produto, cada criterio, os pontos e o que
/// esta marcado como nao observado.
///
/// Passar isso e o que separa "escreva o argumento para esta conta" de "decida
/// qual produto vender". A primeira e a tarefa; a segunda quebraria o ADR-0005.
/// </summary>
public interface IProductPitchPromptBuilder
{
    string AgentName { get; }
    string PromptVersion { get; }

    string BuildSystemPrompt();

    string BuildUserPrompt(AccountContext context, IReadOnlyList<ProductFit> fits);

    string BuildRepairPrompt(
        AccountContext context, IReadOnlyList<ProductFit> fits, string previousOutput, string violations);
}
