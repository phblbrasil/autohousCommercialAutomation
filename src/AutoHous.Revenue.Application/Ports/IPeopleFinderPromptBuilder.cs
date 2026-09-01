using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Monta os prompts do People Finder (A05).
///
/// A assinatura carrega as PERSONAS A PROCURAR, e nao so o contexto da conta.
/// Sem elas o agente sairia buscando "quem decide" em abstrato e devolveria o
/// organograma inteiro - vinte pessoas, das quais duas interessam, cada uma
/// custando evidencia, validacao e uma linha de PII no banco.
///
/// As personas saem do produto de entrada escolhido pelo Product Matcher. E o
/// que liga uma etapa a outra: procurar CTO numa conta cujo produto de entrada e
/// FrontCar seria gastar a busca na pessoa errada.
/// </summary>
public interface IPeopleFinderPromptBuilder
{
    string AgentName { get; }
    string PromptVersion { get; }

    string BuildSystemPrompt();

    string BuildUserPrompt(AccountContext context, PeopleSearchBrief brief);

    string BuildRepairPrompt(
        AccountContext context, PeopleSearchBrief brief, string previousOutput, string violations);
}

/// <summary>O que procurar, e por quê.</summary>
public sealed record PeopleSearchBrief
{
    /// <summary>Produto de entrada escolhido pela plataforma. Nulo quando nao ha fit.</summary>
    public string? EntryProduct { get; init; }

    /// <summary>
    /// Cargos a procurar, em ordem de prioridade. Vem do catalogo, possivelmente
    /// restringidos pelo Product Matcher.
    /// </summary>
    public required IReadOnlyList<string> Personas { get; init; }

    /// <summary>
    /// O angulo comercial, quando existir. Nao e para o agente reescreve-lo: e
    /// para ele saber que uma conversa sobre integracao de estoque precisa de
    /// alguem de operacoes, e nao do gerente da loja mais proxima.
    /// </summary>
    public string? Angle { get; init; }
}
