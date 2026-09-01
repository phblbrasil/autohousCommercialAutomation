using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Grava uma auditoria validada em UMA transacao: sources, evidence, a linha de
/// <c>website_audits</c> com as sete notas, a ligacao evidencia-auditoria, as
/// tecnologias, o research_run, o agent_run, o evento de saida e a baixa do
/// evento de entrada.
///
/// Mesma garantia declarada por <see cref="IResearchProfilePersister"/>, e pela
/// mesma razao: nao pode existir estado em que a conta tem nota de auditoria sem
/// as evidencias que a sustentam.
/// </summary>
public interface IWebsiteAuditPersister
{
    Task PersistAsync(WebsiteAuditPersistRequest request, CancellationToken ct = default);
}

public sealed record WebsiteAuditPersistRequest
{
    public required Guid AccountId { get; init; }
    public required Guid ResearchRunId { get; init; }
    public required Guid OutboxEventId { get; init; }

    /// <summary>Medicao da sonda. Vai crua para <c>website_audits.probe</c>.</summary>
    public required WebsiteProbeResult Probe { get; init; }

    /// <summary>
    /// Nulo quando o site nao respondeu: nesse caso nao houve o que o agente
    /// interpretasse, e a auditoria e so a medicao mais o fato de o dominio
    /// estar fora do ar - que ja e informacao comercial.
    /// </summary>
    public WebsiteAuditProfile? Profile { get; init; }

    public required WebsiteAuditScore Score { get; init; }
    public required AgentRun AgentRun { get; init; }
}
