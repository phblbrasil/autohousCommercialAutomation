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

public sealed record CnaeClassification(
    string Code,
    string Description,
    AutomotiveOperation Operation,
    /// <summary>
    /// Se o CNAE pertence ao ICP central da AutoHous (frame 02 da V1: revendas,
    /// concessionarias e grupos). Fora dele a conta ainda pode entrar na base -
    /// e universo adjacente - mas nao entra na fila de prospeccao do piloto.
    /// </summary>
    bool InCoreIcp);

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
            new("4511101", "Comercio a varejo de automoveis, camionetas e utilitarios novos", AutomotiveOperation.Concessionaria, true),
            new("4511102", "Comercio a varejo de automoveis, camionetas e utilitarios usados", AutomotiveOperation.Revenda, true),
            new("4511103", "Comercio por atacado de automoveis, camionetas e utilitarios novos e usados", AutomotiveOperation.Atacado, true),
            new("4511104", "Comercio por atacado de caminhoes novos e usados", AutomotiveOperation.Atacado, true),
            new("4511105", "Comercio por atacado de reboques e semirreboques novos e usados", AutomotiveOperation.Atacado, false),
            new("4511106", "Comercio por atacado de onibus e microonibus novos e usados", AutomotiveOperation.Atacado, false),
            new("4512901", "Representantes comerciais e agentes do comercio de veiculos automotores", AutomotiveOperation.Intermediacao, true),
            new("4512902", "Comercio sob consignacao de veiculos automotores", AutomotiveOperation.Intermediacao, true),
            new("4520001", "Servicos de manutencao e reparacao mecanica de veiculos automotores", AutomotiveOperation.Oficina, false),
            new("4520002", "Servicos de lanternagem ou funilaria e pintura de veiculos automotores", AutomotiveOperation.Oficina, false),
            new("4520005", "Servicos de lavagem, lubrificacao e polimento de veiculos automotores", AutomotiveOperation.Oficina, false),
            new("4530701", "Comercio por atacado de pecas e acessorios novos para veiculos automotores", AutomotiveOperation.Autopecas, false),
            new("4530703", "Comercio a varejo de pecas e acessorios novos para veiculos automotores", AutomotiveOperation.Autopecas, false),
            new("4541201", "Comercio por atacado de motocicletas e motonetas", AutomotiveOperation.Motos, false),
            new("4541203", "Comercio a varejo de motocicletas e motonetas novas", AutomotiveOperation.Motos, true),
            new("4541204", "Comercio a varejo de motocicletas e motonetas usadas", AutomotiveOperation.Motos, true),
            new("4542101", "Representantes comerciais e agentes do comercio de motocicletas", AutomotiveOperation.Intermediacao, false),
            new("7711000", "Locacao de automoveis sem condutor", AutomotiveOperation.Locadora, false)
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
