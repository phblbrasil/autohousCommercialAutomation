using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Grava um Research Profile validado em UMA transacao: sources, evidence,
/// signals, brands, locations, a account, o research_run, o agent_run, o evento
/// de saida e a baixa do evento de entrada.
///
/// O caso de uso declara a garantia transacional; como ela e obtida e detalhe do
/// adaptador (§6.3: todo contrato declara suas garantias transacionais).
/// </summary>
public interface IResearchProfilePersister
{
    Task PersistAsync(ResearchProfilePersistRequest request, CancellationToken ct = default);
}

public sealed record ResearchProfilePersistRequest
{
    public required Guid AccountId { get; init; }
    public required Guid ResearchRunId { get; init; }
    public required Guid OutboxEventId { get; init; }
    public required AccountStatus CurrentStatus { get; init; }
    public required ResearchProfile Profile { get; init; }
    public required AgentRun AgentRun { get; init; }

    /// <summary>Intervalo ate a proxima repesquisa automatica.</summary>
    public TimeSpan ResearchInterval { get; init; } = TimeSpan.FromDays(90);
}
