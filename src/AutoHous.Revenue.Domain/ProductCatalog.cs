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

        // AutoTalk continua sendo CALCULADO e nao e ofertado.
        //
        // A distincao nao e detalhe. Apagar o produto do catalogo apagaria junto
        // o diagnostico: quando ele existir, a pergunta "quantas contas tinham
        // essa dor, e ha quanto tempo?" so tem resposta se a nota tiver sido
        // gravada esse tempo todo. Mante-lo ofertavel, por outro lado, manda o
        // SDR abrir conversa sobre algo que a AutoHous nao tem para vender.
        //
        // Tambem ha um motivo de MEDICAO para nao deixa-lo disputar a entrada
        // agora: o criterio que mais o sustenta - `canal_de_conversa` - paga 30
        // pontos pela AUSENCIA de um widget de chat que a sonda reconheca, e
        // ausencia e a evidencia mais fraca que existe aqui (a sonda so enxerga
        // assinatura que ela conhece; um botao de WhatsApp caseiro nao conta).
        // Com esses pesos ele vencia a porta de entrada em conta grande
        // independentemente de haver dor de atendimento.
        new(AutoTalk,
            "Atendimento e conversacao com o cliente",
            ["Diretor Comercial", "CX", "Operacoes", "Atendimento"],
            Available: false),

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

    /// <summary>
    /// Este produto pode ser OFERTADO hoje?
    ///
    /// Separado de <see cref="Sellable"/> porque as duas perguntas são
    /// diferentes: "isto é produto para a conta prospectada?" e "isto existe
    /// para vender agora?". O Partner Program falha na primeira; o AutoTalk, na
    /// segunda. Produto desconhecido devolve <c>false</c> — na dúvida não se
    /// oferece.
    /// </summary>
    public static bool IsAvailable(string product) =>
        Find(product) is { Available: true };
}

/// <summary>
/// <paramref name="Available"/> distingue "existe no catálogo" de "pode ser
/// oferecido hoje". Um produto indisponível continua sendo pontuado — a nota é o
/// registro de que a dor existia antes de haver o que vender — mas não abre
/// conversa nem recebe argumento do agente.
/// </summary>
public sealed record ProductDefinition(
    string Name,
    string Solves,
    IReadOnlyList<string> Personas,
    bool Available = true);
