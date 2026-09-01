namespace AutoHous.Revenue.Domain;

/// <summary>
/// Os produtos da AutoHous, no dominio.
///
/// Ate esta entrega o catalogo existia em dois lugares que nao se conheciam: a
/// ferramenta MCP <c>get_product_catalog</c>, que o descreve para o agente, e a
/// referencia <c>products.md</c> da skill de pesquisa, que o descreve em prosa.
/// Nenhum dos dois podia ser consultado por quem calcula fit, entao o calculo
/// nao existia.
///
/// Vem para o dominio porque a recomendacao de produto e regra de negocio: qual
/// dor cada produto resolve nao e detalhe de apresentacao nem de fornecedor. A
/// ferramenta MCP passa a ler daqui, e a divergencia entre o que o agente ve e o
/// que a plataforma calcula deixa de ser possivel.
///
/// Nao e tabela por decisao: seis produtos que mudam uma vez por ano nao
/// justificam uma migration por ajuste de persona, e um catalogo em banco
/// tornaria <see cref="ProductFitScoring"/> impuro. Vira tabela no dia em que o
/// pricing entrar.
/// </summary>
public static class ProductCatalog
{
    public const string FrontCar = "FrontCar";
    public const string MotorHub = "MotorHub";
    public const string AutoFollow = "AutoFollow";
    public const string AutoTalk = "AutoTalk";
    public const string BoxTech = "BoxTech";
    public const string PartnerProgram = "Partner Program";

    /// <summary>
    /// O <c>Partner Program</c> nao esta aqui de proposito: ele e canal, e nao
    /// produto para a conta prospectada. Recomenda-lo a uma concessionaria seria
    /// oferecer a ela que revenda a AutoHous. Continua no catalogo publico
    /// porque o agente precisa saber que existe.
    /// </summary>
    public static readonly IReadOnlyList<ProductDefinition> Sellable =
    [
        new(FrontCar,
            "Site, vitrine de estoque, ofertas e landing pages",
            ["Diretor de Marketing", "Gerente de Marketing", "Diretor Comercial", "Head Digital", "Socio"]),

        new(MotorHub,
            "Integracao e distribuicao de estoque entre unidades e canais",
            ["CTO", "CIO", "Head de TI", "Gerente de Sistemas", "Diretor de Operacoes"]),

        new(AutoFollow,
            "Follow-up e gestao de leads comerciais",
            ["Diretor Comercial", "Gerente Comercial", "CRM Manager", "BDC Manager"]),

        new(AutoTalk,
            "Atendimento e conversacao com o cliente",
            ["Diretor Comercial", "CX", "Operacoes", "Atendimento"]),

        new(BoxTech,
            "Plataforma tecnologica para operacoes maiores",
            ["CIO", "CTO", "Head de Digital", "Diretor de Tecnologia"])
    ];

    /// <summary>Inclui o canal. E o que a ferramenta MCP publica.</summary>
    public static readonly IReadOnlyList<ProductDefinition> All =
    [
        .. Sellable,
        new(PartnerProgram,
            "Canal via agencias e integradores",
            ["Socio de agencia", "Head de Novos Negocios"])
    ];

    public static bool IsKnown(string product) =>
        All.Any(p => string.Equals(p.Name, product, StringComparison.OrdinalIgnoreCase));

    public static ProductDefinition? Find(string product) =>
        All.FirstOrDefault(p => string.Equals(p.Name, product, StringComparison.OrdinalIgnoreCase));
}

public sealed record ProductDefinition(
    string Name,
    string Solves,
    IReadOnlyList<string> Personas);
