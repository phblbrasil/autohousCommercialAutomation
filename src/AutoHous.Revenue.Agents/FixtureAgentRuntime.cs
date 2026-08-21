using AutoHous.Revenue.Application;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Runtime deterministico que le respostas gravadas do disco.
///
/// E o que permite desenvolver e testar o slice inteiro sem Hermes instalado,
/// sem chave de provider de modelo, sem custo e sem variabilidade. Todo o CI
/// roda assim; o runtime real e exercitado apenas na ativacao (E11).
/// </summary>
public sealed class FixtureAgentRuntime(
    FixtureAgentRuntimeOptions options,
    ILogger<FixtureAgentRuntime>? logger = null) : IAgentRuntime
{
    public string Name => "fixture";

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        var scenario = request.FixtureScenario ?? options.DefaultScenario;
        var path = Path.Combine(options.RootDirectory, request.AgentName, $"{scenario}.json");

        if (!File.Exists(path))
        {
            return Task.FromResult(AgentRunResult.Failure(
                $"Fixture nao encontrado: {path}. " +
                $"Crie o arquivo ou ajuste FixtureScenario."));
        }

        logger?.LogInformation(
            "Fixture {Scenario} carregado para o agente {AgentName} de {Path}",
            scenario, request.AgentName, path);

        var raw = File.ReadAllText(path);

        return Task.FromResult(new AgentRunResult
        {
            ExternalRunId = $"fixture:{request.AgentName}:{scenario}",
            RawText = raw,
            Succeeded = true,
            ModelProvider = "fixture",
            ModelName = scenario,
            // Contabilizados como zero de proposito: custo de fixture nao pode
            // poluir a metrica de "custo de IA por conta pesquisada".
            InputTokens = 0,
            OutputTokens = 0,
            EstimatedCost = 0m,
            Duration = TimeSpan.Zero
        });
    }
}

public sealed class FixtureAgentRuntimeOptions
{
    public string RootDirectory { get; set; } = "tests/fixtures/agent-runs";
    public string DefaultScenario { get; set; } = "success";
}
