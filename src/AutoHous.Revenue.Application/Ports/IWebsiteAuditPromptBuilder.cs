using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Monta os prompts do Website Auditor.
///
/// Porta propria, e nao <see cref="IResearchPromptBuilder"/> generalizado, por
/// causa da assinatura: o auditor recebe a MEDICAO junto do contexto. Isso nao e
/// detalhe de conveniencia - e o que impede o agente de estimar o que a sonda ja
/// mediu. O prompt mostra o tempo de resposta real, e pede interpretacao, nao
/// numero.
/// </summary>
public interface IWebsiteAuditPromptBuilder
{
    string AgentName { get; }
    string PromptVersion { get; }

    string BuildSystemPrompt();
    string BuildUserPrompt(AccountContext context, WebsiteProbeResult probe);

    string BuildRepairPrompt(
        AccountContext context, WebsiteProbeResult probe, string previousOutput, string violations);
}
