namespace AutoHous.Revenue.Domain;

/// <summary>
/// Tipo de operacao automotiva, derivado do CNAE principal.
///
/// Nao e o mesmo que o segmento comercial do ICP: um grupo com 8 lojas e uma
/// revenda tem o mesmo CNAE e motions completamente diferentes. O CNAE responde
/// "que negocio e este?"; porte e grupo economico respondem "que motion usar?".
/// </summary>
public enum AutomotiveOperation
{
    /// <summary>Veiculos novos: concessionaria de marca.</summary>
    Concessionaria,

    /// <summary>Veiculos usados: revenda / seminovos.</summary>
    Revenda,

    /// <summary>Atacado de veiculos.</summary>
    Atacado,

    /// <summary>Intermediacao, consignacao e representacao comercial.</summary>
    Intermediacao,

    /// <summary>Manutencao e reparacao.</summary>
    Oficina,

    /// <summary>Pecas e acessorios.</summary>
    Autopecas,

    /// <summary>Motocicletas.</summary>
    Motos,

    /// <summary>Locacao de veiculos sem condutor.</summary>
    Locadora
}

/// <summary>
/// Camada de ICP a que o CNAE pertence.
///
/// Era um booleano - dentro ou fora do ICP central - e isso escondia a diferenca
/// que mais importa no que ficava de fora. Na competencia 2026-08, o universo
/// automotivo ativo tem 839.409 estabelecimentos: 94.047 vendem veiculo e
/// 593.022 vivem de manutencao e peca. Tratar os dois grupos como "resto" e
/// jogar 71% do universo numa mesma sacola sem nome.
///
/// A camada nao decide se a conta entra na base: isso e o
/// <see cref="CnaeCatalog"/> inteiro. Ela decide em que fila a conta entra, e
/// com que motion.
/// </summary>
public enum IcpTier
{
    /// <summary>
    /// Quem vende veiculo: concessionaria, revenda, atacado, intermediacao e
    /// motos. E o ICP do piloto (frame 02 da V1) - o comprador natural de
    /// vitrine de estoque, distribuicao e follow-up de lead.
    /// </summary>
    Core,

    /// <summary>
    /// Quem vive da manutencao e da peca: oficina mecanica, funilaria e
    /// autopecas (atacado e varejo).
    ///
    /// Motion diferente do Core, nao menor: o ticket medio e outro, a compra e
    /// menos sobre vitrine de estoque e mais sobre atendimento e recorrencia. E
    /// o maior bloco do universo - por isso tem nome proprio, e nao "fora do
    /// ICP".
    /// </summary>
    Aftermarket,

    /// <summary>
    /// Universo adjacente: lavagem e polimento, locadora, atacado de reboques,
    /// onibus e motos. Entra na base porque e mercado automotivo e o agregado
    /// precisa enxerga-lo, mas nao ha produto AutoHous com encaixe obvio hoje.
    /// Promover um destes a camada propria e uma linha neste arquivo.
    /// </summary>
    Adjacent
}

public sealed record CnaeClassification(
    string Code,
    string Description,
    AutomotiveOperation Operation,
    IcpTier Tier)
{
    /// <summary>
    /// ICP central: quem entra na fila de prospeccao do piloto.
    ///
    /// Continua existindo como propriedade derivada porque a pergunta "e do ICP
    /// central?" e frequente e nao deveria virar comparacao de enum espalhada
    /// pelo codigo.
    /// </summary>
    public bool InCoreIcp => Tier == IcpTier.Core;
}

/// <summary>
/// O universo de CNAEs que a AutoHous prospecta.
///
/// Este catalogo e o primeiro quality gate do pipeline de captura: uma base da
/// Receita traz milhoes de empresas, e importar tudo para depois filtrar custa
/// armazenamento e polui todas as buscas por similaridade de nome. O filtro
/// acontece na entrada.
/// </summary>
public static class CnaeCatalog
{
    private static readonly Dictionary<string, CnaeClassification> ByCode =
        new CnaeClassification[]
        {
            new("4511101", "Comercio a varejo de automoveis, camionetas e utilitarios novos", AutomotiveOperation.Concessionaria, IcpTier.Core),
            new("4511102", "Comercio a varejo de automoveis, camionetas e utilitarios usados", AutomotiveOperation.Revenda, IcpTier.Core),
            new("4511103", "Comercio por atacado de automoveis, camionetas e utilitarios novos e usados", AutomotiveOperation.Atacado, IcpTier.Core),
            new("4511104", "Comercio por atacado de caminhoes novos e usados", AutomotiveOperation.Atacado, IcpTier.Core),
            new("4511105", "Comercio por atacado de reboques e semirreboques novos e usados", AutomotiveOperation.Atacado, IcpTier.Adjacent),
            new("4511106", "Comercio por atacado de onibus e microonibus novos e usados", AutomotiveOperation.Atacado, IcpTier.Adjacent),
            new("4512901", "Representantes comerciais e agentes do comercio de veiculos automotores", AutomotiveOperation.Intermediacao, IcpTier.Core),
            new("4512902", "Comercio sob consignacao de veiculos automotores", AutomotiveOperation.Intermediacao, IcpTier.Core),
            new("4520001", "Servicos de manutencao e reparacao mecanica de veiculos automotores", AutomotiveOperation.Oficina, IcpTier.Aftermarket),
            new("4520002", "Servicos de lanternagem ou funilaria e pintura de veiculos automotores", AutomotiveOperation.Oficina, IcpTier.Aftermarket),
            new("4520005", "Servicos de lavagem, lubrificacao e polimento de veiculos automotores", AutomotiveOperation.Oficina, IcpTier.Adjacent),
            new("4530701", "Comercio por atacado de pecas e acessorios novos para veiculos automotores", AutomotiveOperation.Autopecas, IcpTier.Aftermarket),
            new("4530703", "Comercio a varejo de pecas e acessorios novos para veiculos automotores", AutomotiveOperation.Autopecas, IcpTier.Aftermarket),
            new("4541201", "Comercio por atacado de motocicletas e motonetas", AutomotiveOperation.Motos, IcpTier.Adjacent),
            new("4541203", "Comercio a varejo de motocicletas e motonetas novas", AutomotiveOperation.Motos, IcpTier.Core),
            new("4541204", "Comercio a varejo de motocicletas e motonetas usadas", AutomotiveOperation.Motos, IcpTier.Core),
            new("4542101", "Representantes comerciais e agentes do comercio de motocicletas", AutomotiveOperation.Intermediacao, IcpTier.Adjacent),
            new("7711000", "Locacao de automoveis sem condutor", AutomotiveOperation.Locadora, IcpTier.Adjacent)
        }.ToDictionary(c => c.Code);

    /// <summary>
    /// Normaliza o CNAE para sete digitos. As bases publicas trazem o mesmo
    /// codigo em pelo menos tres formatos - "4511-1/01", "45.11-1-01" e
    /// "4511101" - e comparar string crua faria a mesma empresa cair em ramos
    /// diferentes conforme o arquivo de origem.
    /// </summary>
    public static string? NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);

        return digits.Length == 7 ? digits : null;
    }

    public static CnaeClassification? Classify(string? rawCode)
    {
        var code = NormalizeCode(rawCode);

        return code is not null && ByCode.TryGetValue(code, out var classification)
            ? classification
            : null;
    }

    public static bool IsInUniverse(string? rawCode) => Classify(rawCode) is not null;

    /// <summary>Codigos aceitos, para montar o filtro na origem do seed.</summary>
    public static IReadOnlyCollection<string> Codes => ByCode.Keys;

    /// <summary>A camada de ICP do codigo, ou nulo se ele nem pertence ao universo.</summary>
    public static IcpTier? TierOf(string? rawCode) => Classify(rawCode)?.Tier;

    /// <summary>Codigos de uma camada. Existe para relatorio e para recorte de carga.</summary>
    public static IReadOnlyCollection<string> CodesInTier(IcpTier tier) =>
        [.. ByCode.Values.Where(c => c.Tier == tier).Select(c => c.Code)];

    /// <summary>Segmento gravado em <c>accounts.segment</c>.</summary>
    public static string ToSegment(AutomotiveOperation operation) => operation switch
    {
        AutomotiveOperation.Concessionaria => "concessionaria",
        AutomotiveOperation.Revenda => "revenda",
        AutomotiveOperation.Atacado => "atacado",
        AutomotiveOperation.Intermediacao => "intermediacao",
        AutomotiveOperation.Oficina => "oficina",
        AutomotiveOperation.Autopecas => "autopecas",
        AutomotiveOperation.Motos => "motos",
        AutomotiveOperation.Locadora => "locadora",
        _ => "outro"
    };
}
