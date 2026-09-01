using System.Text.RegularExpressions;

namespace AutoHous.Revenue.Domain;

/// <summary>Como um cargo de texto livre foi entendido.</summary>
public sealed record PersonaMatch(
    string Persona,
    string Seniority,
    string? Department,
    decimal Confidence);

/// <summary>
/// Traduz cargo escrito por gente em persona do catalogo.
///
/// Existe porque as duas pontas falam linguas diferentes. O
/// <see cref="ProductCatalog"/> diz que o MotorHub se vende para "Diretor de
/// Operacoes"; o LinkedIn de uma concessionaria do interior diz "Diretor Geral -
/// Grupo Vento Sul" ou "Resp. Operacional". Sem esta traducao, o People Finder
/// gravaria <c>contacts.persona = 'Resp. Operacional'</c>, a consulta que monta
/// a fila de abordagem por persona nao acharia a linha, e o contato existiria no
/// banco sem nunca ser usado.
///
/// A classificacao e por palavra-chave e nao por modelo, pela mesma razao do
/// ADR-0005: precisa ser reproduzivel e barata. Um cargo que nao casa com nada
/// devolve <c>null</c> - e <c>null</c> vira <c>persona</c> nula no banco, que e
/// honesto, em vez de um chute que parece dado.
/// </summary>
public static partial class PersonaCatalog
{
    public static class Seniorities
    {
        public const string Socio = "socio";
        public const string CLevel = "c_level";
        public const string Diretor = "diretor";
        public const string Gerente = "gerente";
        public const string Coordenador = "coordenador";
        public const string Analista = "analista";
        public const string Outro = "outro";
    }

    /// <summary>
    /// Senioridades que decidem compra. E o que o Opportunity Score chama de
    /// "decisor identificado" nos 5 pontos de contactability.
    /// </summary>
    public static bool IsDecisionMaker(string? seniority) =>
        seniority is Seniorities.Socio or Seniorities.CLevel or Seniorities.Diretor;

    /// <summary>Toda persona que algum produto do catalogo persegue, sem repeticao.</summary>
    public static IReadOnlyList<string> Canonical { get; } =
        [.. ProductCatalog.All
            .SelectMany(p => p.Personas)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>
    /// Regras em ORDEM: a primeira que casa vence. A ordem e a regra - "Diretor
    /// de Marketing" tem que ser testado antes de "Diretor Comercial" e antes de
    /// "Socio", senao um titulo composto cai no rotulo mais generico e a fila de
    /// marketing fica vazia enquanto a comercial enche de gente errada.
    /// </summary>
    private static readonly (Regex Pattern, string Persona, string Seniority, string? Department, decimal Confidence)[] Rules =
    [
        // -------------------------------------------------------- tecnologia
        (Cto(),          "CTO",                    Seniorities.CLevel,      "tecnologia", 0.95m),
        (Cio(),          "CIO",                    Seniorities.CLevel,      "tecnologia", 0.95m),
        (HeadTi(),       "Head de TI",             Seniorities.Diretor,     "tecnologia", 0.85m),
        (DiretorTec(),   "Diretor de Tecnologia",  Seniorities.Diretor,     "tecnologia", 0.9m),
        (GerenteSis(),   "Gerente de Sistemas",    Seniorities.Gerente,     "tecnologia", 0.85m),

        // --------------------------------------------------------- marketing
        (DiretorMkt(),   "Diretor de Marketing",   Seniorities.Diretor,     "marketing",  0.9m),
        (GerenteMkt(),   "Gerente de Marketing",   Seniorities.Gerente,     "marketing",  0.9m),
        (HeadDigital(),  "Head Digital",           Seniorities.Diretor,     "marketing",  0.8m),

        // ---------------------------------------------------------- comercial
        (CrmManager(),   "CRM Manager",            Seniorities.Coordenador, "comercial",  0.8m),
        (Bdc(),          "BDC Manager",            Seniorities.Coordenador, "comercial",  0.8m),
        (DiretorCom(),   "Diretor Comercial",      Seniorities.Diretor,     "comercial",  0.9m),
        (GerenteCom(),   "Gerente Comercial",      Seniorities.Gerente,     "comercial",  0.9m),

        // --------------------------------------------------------- operacoes
        (DiretorOps(),   "Diretor de Operacoes",   Seniorities.Diretor,     "operacoes",  0.9m),
        (Cx(),           "CX",                     Seniorities.Coordenador, "atendimento", 0.7m),
        (Atendimento(),  "Atendimento",            Seniorities.Analista,    "atendimento", 0.7m),
        (Operacoes(),    "Operacoes",              Seniorities.Gerente,     "operacoes",  0.65m),

        // -------------------------------------------------------------- dono
        // Por ultimo de proposito: "Socio-Diretor Comercial" deve virar Diretor
        // Comercial, que e mais especifico e diz com quem falar sobre o que.
        (Socio(),        "Socio",                  Seniorities.Socio,       null,         0.85m)
    ];

    public static PersonaMatch? Classify(string? jobTitle)
    {
        if (string.IsNullOrWhiteSpace(jobTitle)) return null;

        var normalized = NameNormalizer.Normalize(jobTitle);

        if (normalized.Length == 0) return null;

        foreach (var (pattern, persona, seniority, department, confidence) in Rules)
        {
            if (pattern.IsMatch(normalized))
            {
                return new PersonaMatch(persona, seniority, department, confidence);
            }
        }

        // Cargo nao reconhecido ainda tem senioridade util: saber que e um
        // diretor de ALGUMA coisa ja muda a prioridade da conta, mesmo sem
        // saber de que area.
        var fallback = SeniorityOnly(normalized);

        return fallback is null ? null : new PersonaMatch(string.Empty, fallback, null, 0.4m);
    }

    private static string? SeniorityOnly(string normalized) => normalized switch
    {
        _ when Socio().IsMatch(normalized) => Seniorities.Socio,
        _ when AnyCLevel().IsMatch(normalized) => Seniorities.CLevel,
        _ when AnyDiretor().IsMatch(normalized) => Seniorities.Diretor,
        _ when AnyGerente().IsMatch(normalized) => Seniorities.Gerente,
        _ when AnyCoordenador().IsMatch(normalized) => Seniorities.Coordenador,
        _ when AnyAnalista().IsMatch(normalized) => Seniorities.Analista,
        _ => null
    };

    // O padrao roda sobre a saida do NameNormalizer: maiusculas, sem acento e
    // sem pontuacao. Por isso "C T O" casa - "C.T.O." vira isso.
    [GeneratedRegex(@"\bC ?T ?O\b|\bCHIEF TECHNOLOGY\b")] private static partial Regex Cto();
    [GeneratedRegex(@"\bC ?I ?O\b|\bCHIEF INFORMATION\b")] private static partial Regex Cio();
    [GeneratedRegex(@"\bHEAD\b.*\b(TI|IT|IINFRA|INFRA)\b")] private static partial Regex HeadTi();
    [GeneratedRegex(@"\bDIRETOR[A]?\b.*\b(TECNOLOGIA|TI|IT)\b")] private static partial Regex DiretorTec();
    [GeneratedRegex(@"\bGERENTE\b.*\b(SISTEMAS|TI|IT|TECNOLOGIA)\b")] private static partial Regex GerenteSis();

    [GeneratedRegex(@"\bDIRETOR[A]?\b.*\b(MARKETING|MKT)\b")] private static partial Regex DiretorMkt();
    [GeneratedRegex(@"\bGERENTE\b.*\b(MARKETING|MKT)\b")] private static partial Regex GerenteMkt();
    [GeneratedRegex(@"\bHEAD\b.*\b(DIGITAL|ECOMMERCE|E COMMERCE)\b|\bDIRETOR[A]?\b.*\bDIGITAL\b")] private static partial Regex HeadDigital();

    [GeneratedRegex(@"\bCRM\b")] private static partial Regex CrmManager();
    [GeneratedRegex(@"\bBDC\b|\bBUSINESS DEVELOPMENT CENTER\b")] private static partial Regex Bdc();
    [GeneratedRegex(@"\bDIRETOR[A]?\b.*\b(COMERCIAL|VENDAS|SALES)\b|\bC ?R ?O\b")] private static partial Regex DiretorCom();
    [GeneratedRegex(@"\bGERENTE\b.*\b(COMERCIAL|VENDAS|SALES|LOJA)\b")] private static partial Regex GerenteCom();

    [GeneratedRegex(@"\bDIRETOR[A]?\b.*\b(OPERACOES|OPERACIONAL|OPERATIONS)\b|\bC ?O ?O\b")] private static partial Regex DiretorOps();
    [GeneratedRegex(@"\bC ?X\b|\bCUSTOMER EXPERIENCE\b|\bEXPERIENCIA DO CLIENTE\b")] private static partial Regex Cx();
    [GeneratedRegex(@"\bATENDIMENTO\b|\bPOS VENDA\b|\bSAC\b")] private static partial Regex Atendimento();
    [GeneratedRegex(@"\bOPERACOES\b|\bOPERACIONAL\b")] private static partial Regex Operacoes();

    [GeneratedRegex(@"\bSOCIO[A]?\b|\bPROPRIETARIO[A]?\b|\bFUNDADOR[A]?\b|\bFOUNDER\b|\bOWNER\b|\bDONO[A]?\b")] private static partial Regex Socio();

    [GeneratedRegex(@"\bC ?[A-Z] ?O\b|\bCHIEF\b|\bPRESIDENTE\b")] private static partial Regex AnyCLevel();
    [GeneratedRegex(@"\bDIRETOR[A]?\b|\bHEAD\b|\bVP\b|\bVICE PRESIDENTE\b")] private static partial Regex AnyDiretor();
    [GeneratedRegex(@"\bGERENTE\b|\bMANAGER\b")] private static partial Regex AnyGerente();
    [GeneratedRegex(@"\bCOORDENADOR[A]?\b|\bSUPERVISOR[A]?\b|\bLIDER\b")] private static partial Regex AnyCoordenador();
    [GeneratedRegex(@"\bANALISTA\b|\bASSISTENTE\b|\bAUXILIAR\b|\bCONSULTOR[A]?\b|\bVENDEDOR[A]?\b")] private static partial Regex AnyAnalista();
}
