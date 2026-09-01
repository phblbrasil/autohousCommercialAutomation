using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Grava os contatos descobertos em UMA transacao: sources, evidence, as linhas
/// de <c>contacts</c> e <c>contact_channels</c>, a ligacao contato-evidencia, o
/// research_run, o agent_run, o evento de saida e a baixa do evento de entrada.
///
/// A garantia importa mais aqui do que nos outros persisters. Um contato gravado
/// sem a evidencia que o sustenta nao e um dado incompleto: e o nome de uma
/// pessoa afirmado como funcionaria de uma empresa sem que exista onde isso foi
/// visto - e o passo seguinte do funil escreve para ela.
/// </summary>
public interface IContactPersister
{
    Task<ContactPersistResult> PersistAsync(ContactPersistRequest request, CancellationToken ct = default);
}

public sealed record ContactPersistRequest
{
    public required Guid AccountId { get; init; }
    public required Guid ResearchRunId { get; init; }
    public required Guid OutboxEventId { get; init; }

    public required ContactDiscoveryProfile Profile { get; init; }

    /// <summary>
    /// Dominio da conta, para <see cref="ContactPolicy.MatchesAccountDomain"/>.
    /// Um e-mail no dominio da propria empresa e o lastro mais forte de vinculo
    /// que existe sem depender do que o modelo afirmou.
    /// </summary>
    public string? AccountDomain { get; init; }

    public required AgentRun AgentRun { get; init; }
}

public sealed record ContactPersistResult
{
    /// <summary>Contatos efetivamente gravados, depois dos filtros de politica.</summary>
    public required int ContactsPersisted { get; init; }

    public required int ChannelsPersisted { get; init; }

    /// <summary>
    /// Ha decisor entre os gravados. Sai daqui e nao de uma releitura porque o
    /// persister e quem aplicou <see cref="PersonaCatalog.Classify"/> - releitura
    /// significaria classificar duas vezes, com a chance de as duas divergirem.
    /// </summary>
    public required bool HasDecisionMaker { get; init; }

    /// <summary>Canais recusados pela politica de PII, para o log e para o run.</summary>
    public IReadOnlyList<string> RejectedByPolicy { get; init; } = [];
}
