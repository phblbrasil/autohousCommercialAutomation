using System.Reflection;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Architecture.Tests;

/// <summary>
/// Forma dos casos de uso.
///
/// A regra do §5.2 e curta: caso de uso coordena dominio e portas, e nada mais.
/// Um construtor que aceita uma classe concreta de infraestrutura e a primeira
/// pedra do caminho de volta — e o compilador nao reclama, porque a Application
/// nem enxerga o tipo errado. Estes testes reclamam.
/// </summary>
public class UseCaseShapeTests
{
    private static Assembly Application => typeof(Revenue.Application.IAccountRepository).Assembly;

    private static IEnumerable<Type> UseCases => Application
        .GetExportedTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("UseCase", StringComparison.Ordinal));

    [Fact]
    public void Existe_pelo_menos_um_caso_de_uso()
    {
        // Guarda contra os demais testes passarem por vacuidade se o sufixo mudar.
        Assert.NotEmpty(UseCases);
    }

    [Fact]
    public void Casos_de_uso_dependem_apenas_de_abstracoes()
    {
        var offenders = new List<string>();

        foreach (var useCase in UseCases)
        {
            var constructor = useCase.GetConstructors().Single();

            foreach (var parameter in constructor.GetParameters())
            {
                var type = parameter.ParameterType;

                var acceptable =
                    type.IsInterface ||
                    type.IsValueType ||
                    type == typeof(string) ||
                    typeof(ILogger).IsAssignableFrom(type);

                if (!acceptable)
                {
                    offenders.Add($"{useCase.Name}.{parameter.Name} : {type.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Casos de uso devem receber portas, nao implementacoes: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// Um caso de uso que devolve <c>Task</c> sem resultado obriga o chamador a
    /// adivinhar o que aconteceu — e o adaptador HTTP precisa da diferenca entre
    /// "conta suprimida" e "cooldown ativo" para escolher o status certo.
    ///
    /// Os casos de uso que RODAM AGENTE sob o outbox sao as excecoes
    /// conscientes: neles a sinalizacao de falha e a excecao que dispara o
    /// reagendamento com backoff.
    /// </summary>
    [Fact]
    public void Casos_de_uso_devolvem_resultado_explicito()
    {
        // Os quatro casos de uso que rodam SOB O OUTBOX, um por agente. Em
        // todos, a sinalizacao de falha e a excecao - e ela que dispara o
        // reagendamento com backoff e, esgotadas as tentativas, o dead-letter.
        // Devolver resultado aqui faria o dispatcher ter de traduzir resultado
        // em excecao para obter o mesmo efeito.
        //
        // DecideNextActionUseCase NAO esta na lista, ainda que tambem rode sob
        // o outbox: ele nao chama agente, nao tem custo e sempre tem uma
        // resposta util - qual foi a decisao e por que. O dispatcher registra
        // isso em log, e e o unico rastro de por que uma conta parou onde
        // parou.
        string[] exempt =
        [
            "ExecuteResearchRunUseCase",
            "ExecuteWebsiteAuditUseCase",
            "MatchProductsUseCase",
            "ExecutePeopleFinderUseCase"
        ];

        var offenders = UseCases
            .Where(t => !exempt.Contains(t.Name))
            .Select(t => (Type: t, Method: t.GetMethod("ExecuteAsync")))
            .Where(x => x.Method is not null && x.Method.ReturnType == typeof(Task))
            .Select(x => x.Type.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Estes casos de uso nao devolvem resultado: {string.Join(", ", offenders)}");
    }
}
