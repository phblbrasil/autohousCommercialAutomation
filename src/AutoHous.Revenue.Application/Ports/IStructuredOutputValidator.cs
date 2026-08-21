using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Valida texto livre de LLM contra um contrato e devolve o objeto tipado.
///
/// Porta e nao classe concreta porque o caso de uso de pesquisa precisa da
/// capacidade "transformar texto em ResearchProfile valido", nao da
/// implementacao "JSON Schema draft 2020-12 via JsonSchema.Net" - que e detalhe
/// de biblioteca e vive em <c>AutoHous.Revenue.Agents</c>.
/// </summary>
public interface IStructuredOutputValidator
{
    ValidationOutcome<T> Validate<T>(string rawText);
}

public sealed record ValidationOutcome<T>
{
    public bool IsValid => Violations.Count == 0 && Value is not null;
    public T? Value { get; init; }
    public IReadOnlyList<SchemaViolation> Violations { get; init; } = [];

    public static ValidationOutcome<T> Ok(T value) => new() { Value = value };

    public static ValidationOutcome<T> Fail(params SchemaViolation[] violations) =>
        new() { Violations = violations };

    public static ValidationOutcome<T> Fail(IReadOnlyList<SchemaViolation> violations) =>
        new() { Violations = violations };

    /// <summary>Resumo legivel usado no prompt de reparo e em research_runs.error.</summary>
    public string Describe() => string.Join("\n", Violations.Select(v => $"- {v}"));
}
