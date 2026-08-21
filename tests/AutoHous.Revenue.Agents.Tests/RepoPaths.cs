namespace AutoHous.Revenue.Agents.Tests;

/// <summary>
/// Resolve caminhos do repositorio a partir do diretorio de saida dos testes,
/// para que schema e fixtures sejam os arquivos REAIS - nao copias que podem
/// divergir silenciosamente do que roda em producao.
/// </summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Schema(string name) =>
        Path.Combine(Root, "hermes", "schemas", name);

    public static string FixtureDirectory =>
        Path.Combine(Root, "tests", "fixtures", "agent-runs");

    public static string Fixture(string agent, string scenario) =>
        Path.Combine(FixtureDirectory, agent, $"{scenario}.json");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "hermes", "schemas")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Raiz do repositorio nao encontrada a partir de {AppContext.BaseDirectory}.");
    }
}
