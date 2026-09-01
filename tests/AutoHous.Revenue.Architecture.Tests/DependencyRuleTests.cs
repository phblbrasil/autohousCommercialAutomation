using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

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
    private const string Domain = "AutoHous.Revenue.Domain";
    private const string Application = "AutoHous.Revenue.Application";
    private const string Infrastructure = "AutoHous.Revenue.Infrastructure";
    private const string Agents = "AutoHous.Revenue.Agents";
    private const string ReceitaFederal = "AutoHous.Revenue.ReceitaFederal";
    private const string WebAudit = "AutoHous.Revenue.WebAudit";

    /// <summary>Pacotes que caracterizam infraestrutura em qualquer forma.</summary>
    private static readonly string[] InfrastructurePackages =
        ["Npgsql", "Dapper", "Microsoft.AspNetCore", "Json.Schema", "JsonSchema.Net", "ModelContextProtocol", "dbup"];

    /// <summary>
    /// Le as referencias direto dos metadados do arquivo, sem carregar o
    /// assembly como codigo.
    ///
    /// A regra so precisa do que o compilador gravou; executar as camadas de
    /// producao dentro do processo de teste seria efeito colateral gratuito. E
    /// ha um ganho pratico: uma politica de Application Control - o Smart App
    /// Control do Windows, por exemplo - pode bloquear o LOAD de um binario
    /// recem-compilado, e a regra de arquitetura sumiria junto com ele. Ler
    /// metadado nao passa pelo loader do runtime.
    /// </summary>
    private static string[] ReferencesOf(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");

        Assert.True(File.Exists(path), $"Assembly ausente ao lado do teste: {path}");

        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);

        var metadata = pe.GetMetadataReader();

        return [.. metadata.AssemblyReferences.Select(handle =>
            metadata.GetString(metadata.GetAssemblyReference(handle).Name))];
    }

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
        // Esta regra fala de TIPOS, e nao de referencias: aqui o assembly
        // precisa mesmo estar carregado. O typeof ja o traz, sem Assembly.Load.
        var offenders = typeof(Revenue.Infrastructure.AccountRepository).Assembly
            .GetExportedTypes()
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
        var offenders = ReferencesOf("AutoHous.Revenue.Api")
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
        var references = ReferencesOf("AutoHous.Revenue.Mcp");

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

    /// <summary>
    /// A sonda de site (A03) faz HTTP e le HTML — e nao pode alcancar o banco.
    ///
    /// A regra existe agora, com a sonda ainda sendo HTTP puro, porque e agora
    /// que ela e barata de impor. Quando o headless browser entrar atras da
    /// mesma porta, ele traz Playwright e umas centenas de MB de Chromium; se a
    /// fronteira nao estiver posta antes, o caminho de menor resistencia sera
    /// referenciar a Infrastructure "so para gravar o resultado direto" — e a
    /// porta IWebsiteProbe deixa de ter proposito.
    /// </summary>
    [Fact]
    public void Sonda_de_site_implementa_portas_sem_conhecer_persistencia()
    {
        var references = ReferencesOf(WebAudit);

        Assert.Contains("AutoHous.Revenue.Application", references);
        Assert.DoesNotContain("AutoHous.Revenue.Infrastructure", references);
        Assert.DoesNotContain("Npgsql", references);
        Assert.DoesNotContain("Dapper", references);
    }

    /// <summary>
    /// A Application declara IWebsiteProbe e nao pode enxergar quem a implementa.
    /// </summary>
    [Fact]
    public void Aplicacao_nao_conhece_a_sonda_de_site()
    {
        Assert.DoesNotContain("AutoHous.Revenue.WebAudit", ReferencesOf(Application));
    }
}
