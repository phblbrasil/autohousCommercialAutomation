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
    /// <c>ExecuteResearchRunUseCase</c> e a excecao consciente: ele roda sob o
    /// outbox, onde a sinalizacao de falha e a excecao que dispara o
    /// reagendamento com backoff.
    /// </summary>
    [Fact]
    public void Casos_de_uso_devolvem_resultado_explicito()
    {
        string[] exempt = ["ExecuteResearchRunUseCase"];

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
