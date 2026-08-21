using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Application;
namespace AutoHous.Revenue.Agents.Tests;

public class FixtureAgentRuntimeTests
{
    private static FixtureAgentRuntime Runtime() =>
        new(new FixtureAgentRuntimeOptions { RootDirectory = RepoPaths.FixtureDirectory });

    private static AgentRunRequest Request(string? scenario = null) => new()
    {
        AgentName = "researcher",
        PromptVersion = "researcher-v1",
        SystemPrompt = "system",
        UserPrompt = "user",
        FixtureScenario = scenario
    };

    [Fact]
    public async Task Carrega_o_cenario_padrao()
    {
        var result = await Runtime().RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("dealer_group", result.RawText);
        Assert.Equal("fixture:researcher:success", result.ExternalRunId);
    }

    [Fact]
    public async Task Carrega_cenario_explicito()
    {
        var result = await Runtime().RunAsync(Request("malformed"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("truncada", result.RawText);
    }

    [Fact]
    public async Task Falha_de_forma_legivel_quando_o_cenario_nao_existe()
    {
        var result = await Runtime().RunAsync(Request("inexistente"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Fixture nao encontrado", result.Error);
    }

    [Fact]
    public async Task Nao_contabiliza_custo()
    {
        // Custo de fixture nao pode poluir a metrica de custo de IA por conta.
        var result = await Runtime().RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0m, result.EstimatedCost);
        Assert.Equal(0, result.InputTokens);
    }

    [Fact]
    public void Identifica_se_como_fixture()
    {
        Assert.Equal("fixture", Runtime().Name);
    }
}
