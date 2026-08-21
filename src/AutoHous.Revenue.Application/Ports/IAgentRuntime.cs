namespace AutoHous.Revenue.Application;

/// <summary>
/// Superficie unica de execucao de agente.
///
/// A porta vive na Application e as implementacoes (Hermes HTTP, fixture) vivem
/// em <c>AutoHous.Revenue.Agents</c>: quem precisa da capacidade declara o
/// contrato, quem fornece a implementacao o satisfaz (§8.5).
///
/// Nota: o Hermes NAO endereca agentes por nome - POST /v1/runs nao recebe "qual
/// agente executar" e delegate_task e decidido pelo modelo em runtime. Portanto
/// <see cref="AgentRunRequest.AgentName"/> e um conceito desta aplicacao: ele
/// seleciona prompt e schema, e rotula agent_runs.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>Nome do runtime, para log e diagnostico ("hermes" | "fixture").</summary>
    string Name { get; }

    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default);
}

public sealed record AgentRunRequest
{
    public required string AgentName { get; init; }
    public required string PromptVersion { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }

    /// <summary>Correlaciona com research_runs; vai no header X-Hermes-Session-Id.</summary>
    public string? SessionId { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Cenario do fixture. Ignorado pelo runtime real.</summary>
    public string? FixtureScenario { get; init; }
}

public sealed record AgentRunResult
{
    /// <summary>Identificador do run no runtime externo (run_id do Hermes).</summary>
    public string? ExternalRunId { get; init; }

    /// <summary>Texto final do assistente, ainda nao validado.</summary>
    public required string RawText { get; init; }

    public required bool Succeeded { get; init; }
    public string? Error { get; init; }

    public string? ModelProvider { get; init; }
    public string? ModelName { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public decimal? EstimatedCost { get; init; }
    public TimeSpan Duration { get; init; }

    public static AgentRunResult Failure(string error, string? externalRunId = null) => new()
    {
        RawText = string.Empty,
        Succeeded = false,
        Error = error,
        ExternalRunId = externalRunId
    };
}

/// <summary>Lancada quando o runtime externo falha de forma nao recuperavel.</summary>
public sealed class AgentRuntimeException(string message, Exception? inner = null)
    : Exception(message, inner);
