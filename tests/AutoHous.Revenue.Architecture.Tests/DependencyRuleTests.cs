using System.Reflection;

namespace AutoHous.Revenue.Architecture.Tests;

/// <summary>
/// As invariantes do §29 da skill de arquitetura, impostas pelo build.
///
/// Existe porque "arquitetura descrita em documento, mas nao imposta pelo
/// codigo" e um antipadrao explicito do §24. Um comentario dizendo que o dominio
/// nao conhece banco nao impede ninguem de adicionar um <c>using Npgsql</c>; este
/// teste impede.
///
/// A verificacao usa as referencias reais do assembly compilado, e nao o
/// <c>.csproj</c>: o compilador descarta referencia declarada e nao usada, entao
/// o que sobra e uso de fato.
/// </summary>
public class DependencyRuleTests
{
    private static Assembly Domain => typeof(Revenue.Domain.Account).Assembly;
    private static Assembly Application => typeof(Revenue.Application.IAccountRepository).Assembly;
    private static Assembly Infrastructure => typeof(Revenue.Infrastructure.AccountRepository).Assembly;
    private static Assembly Agents => typeof(Revenue.Agents.HermesAgentRuntime).Assembly;
    private static Assembly ReceitaFederal => typeof(ReceitaFederal.ReceitaFederalArchive).Assembly;

    /// <summary>Pacotes que caracterizam infraestrutura em qualquer forma.</summary>
    private static readonly string[] InfrastructurePackages =
        ["Npgsql", "Dapper", "Microsoft.AspNetCore", "Json.Schema", "JsonSchema.Net", "ModelContextProtocol", "dbup"];

    private static string[] ReferencesOf(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty)];

    // -------------------------------------------------------------- dominio

    [Fact]
    public void Dominio_nao_referencia_nada_alem_da_biblioteca_padrao()
    {
        var offenders = ReferencesOf(Domain)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                        && name != "System"
                        && name != "netstandard"
                        && name != "mscorlib")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"O dominio deve ser livre de I/O e de framework. Referencias indevidas: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Dominio_nao_conhece_a_aplicacao()
    {
        Assert.DoesNotContain("AutoHous.Revenue.Application", ReferencesOf(Domain));
    }

    // ------------------------------------------------------------ aplicacao

    [Fact]
    public void Aplicacao_nao_referencia_infraestrutura_concreta()
    {
        var offenders = ReferencesOf(Application)
            .Where(name => InfrastructurePackages.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        || name == "AutoHous.Revenue.Infrastructure"
                        || name == "AutoHous.Revenue.Agents")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"A Application so pode depender de portas. Referencias indevidas: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Aplicacao_nao_faz_rede_por_conta_propria()
    {
        Assert.DoesNotContain("System.Net.Http", ReferencesOf(Application));
    }

    // ------------------------------------------------------- infraestrutura

    [Fact]
    public void Infraestrutura_depende_da_aplicacao_e_nao_o_contrario()
    {
        Assert.Contains("AutoHous.Revenue.Application", ReferencesOf(Infrastructure));
        Assert.DoesNotContain("AutoHous.Revenue.Infrastructure", ReferencesOf(Application));
    }

    /// <summary>
    /// Toda porta vive na Application. Uma interface publica nascendo na
    /// infraestrutura e o comeco do caminho de volta: o consumidor passa a
    /// referenciar o assembly do fornecedor para enxergar o contrato.
    /// </summary>
    [Fact]
    public void Infraestrutura_nao_declara_portas_publicas()
    {
        var offenders = Infrastructure.GetExportedTypes()
            .Where(t => t.IsInterface)
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Portas pertencem a Application. Interfaces publicas na Infrastructure: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// O contrato de unidade de trabalho nao pode expor tipo de fornecedor:
    /// enquanto expuser, nenhum caso de uso e testavel sem Postgres.
    /// </summary>
    [Fact]
    public void Unidade_de_trabalho_nao_expoe_tipo_de_fornecedor()
    {
        var leaked = typeof(Revenue.Application.IUnitOfWork)
            .GetMembers()
            .Select(m => m switch
            {
                PropertyInfo p => p.PropertyType,
                MethodInfo mi => mi.ReturnType,
                _ => null
            })
            .Where(t => t is not null && t.Namespace?.StartsWith("Npgsql", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Empty(leaked);
    }

    // ---------------------------------------------------------------- bordas

    [Fact]
    public void Api_nao_acessa_o_banco_diretamente()
    {
        var api = Assembly.Load("AutoHous.Revenue.Api");

        var offenders = ReferencesOf(api)
            .Where(name => name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Dapper", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Controllers nao acessam banco. A API referencia: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// O MCP fala HTTP com a Revenue API. A fronteira e estrutural: sem
    /// referencia a Npgsql, nao existe caminho de codigo em que o agente alcance
    /// o banco, com ou sem credencial.
    /// </summary>
    [Fact]
    public void Mcp_nao_alcanca_o_banco()
    {
        var mcp = Assembly.Load("AutoHous.Revenue.Mcp");
        var references = ReferencesOf(mcp);

        Assert.DoesNotContain("Npgsql", references);
        Assert.DoesNotContain("Dapper", references);
        Assert.DoesNotContain("AutoHous.Revenue.Infrastructure", references);
    }

    [Fact]
    public void Agentes_implementam_portas_da_aplicacao_sem_conhecer_persistencia()
    {
        var references = ReferencesOf(Agents);

        Assert.Contains("AutoHous.Revenue.Application", references);
        Assert.DoesNotContain("AutoHous.Revenue.Infrastructure", references);
        Assert.DoesNotContain("Npgsql", references);
    }

    /// <summary>
    /// O adaptador da Receita faz rede, abre zip e decodifica ISO-8859-1 — e
    /// nada disso pode alcancar o banco.
    ///
    /// A fronteira importa mais aqui do que em qualquer outro adaptador: e o
    /// unico componente que le 7 GB de fonte externa. Se ele puder escrever
    /// direto em companies_cnpj, o filtro de CNAE, a normalizacao e o account
    /// graph viram etapas opcionais que alguem um dia vai pular "so nesta carga".
    /// </summary>
    [Fact]
    public void Fonte_da_receita_implementa_portas_sem_conhecer_persistencia()
    {
        var references = ReferencesOf(ReceitaFederal);

        Assert.Contains("AutoHous.Revenue.Application", references);
        Assert.DoesNotContain("AutoHous.Revenue.Infrastructure", references);
        Assert.DoesNotContain("Npgsql", references);
        Assert.DoesNotContain("Dapper", references);
    }

    /// <summary>
    /// A Application declara as portas da camada 01 e nao pode enxergar quem as
    /// implementa. Sem esta regra, "o caso de uso da carga precisa de um detalhe
    /// do zip" resolveria com uma referencia — e a inversao de dependencia se
    /// perderia no primeiro prazo apertado.
    /// </summary>
    [Fact]
    public void Aplicacao_nao_conhece_o_adaptador_da_receita()
    {
        Assert.DoesNotContain("AutoHous.Revenue.ReceitaFederal", ReferencesOf(Application));
    }
}
