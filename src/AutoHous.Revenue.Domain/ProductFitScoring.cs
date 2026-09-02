namespace AutoHous.Revenue.Domain;

/// <summary>
/// Uma tecnologia ja REGISTRADA para a conta, do jeito que a 0015 a guarda.
///
/// Nao e <see cref="DetectedTechnology"/>: aquele e o achado da sonda, com o
/// trecho de HTML que o denunciou, e existe antes de qualquer escrita. Este e o
/// que sobreviveu a persistencia, e carrega <c>Source</c> - a diferenca entre um
/// pixel medido por regex e um "eles usam Salesforce" deduzido de uma vaga de
/// emprego. So o primeiro e verificavel, e o fit precisa saber qual dos dois
/// esta lendo.
/// </summary>
public sealed record AccountTechnology(string Category, string Name, string Source, decimal Confidence);

/// <summary>
/// O que a auditoria observou, com mais detalhe do que
/// <see cref="WebsiteAuditFacts"/> carrega.
///
/// Sao dois recortes da mesma auditoria porque respondem a perguntas diferentes.
/// O Opportunity Score pergunta "quanta dor esta conta tem?" e precisa de quatro
/// numeros. O fit de produto pergunta "dor de QUE?", e a diferenca entre uma
/// vitrine ruim e um atendimento ausente decide FrontCar contra AutoTalk. Fundir
/// os dois faria o scoring geral carregar campos que ele nunca le.
/// </summary>
public sealed record WebsiteAuditDetail
{
    public bool Reachable { get; init; } = true;

    public decimal? Performance { get; init; }
    public decimal? Seo { get; init; }
    public decimal? Ux { get; init; }
    public decimal? Mobile { get; init; }
    public decimal? Conversion { get; init; }
    public decimal? Inventory { get; init; }
    public decimal? Tracking { get; init; }

    public bool? MultiplePortals { get; init; }
    public bool? ComplexIntegration { get; init; }

    /// <summary>Quantos portais externos a auditoria encontrou. Zero e diferente de nao verificado.</summary>
    public int? PortalCount { get; init; }
}

public sealed record ProductFitInputs
{
    public required DateTimeOffset ReferenceDate { get; init; }

    public AutomotiveOperation? Operation { get; init; }
    public int? StoreCount { get; init; }
    public int? InventoryEstimate { get; init; }
    public int CnpjCount { get; init; } = 1;
    public int BrandCount { get; init; }
    public bool HasAuthorizedBrand { get; init; }

    /// <summary>Nulo enquanto o site nao foi auditado - nao "site perfeito".</summary>
    public WebsiteAuditDetail? Audit { get; init; }

    public IReadOnlyList<AccountTechnology> Technologies { get; init; } = [];
    public IReadOnlyList<ScoredSignal> Signals { get; init; } = [];
}

/// <summary>
/// Uma linha do porque. <c>Observed=false</c> e "ainda nao sabemos", que nao e
/// "vale zero" - a mesma distincao do <see cref="ScoreComponent"/>, e pelo mesmo
/// motivo: um fit de 20 sem auditoria e pedido de auditoria, nao veredito de que
/// o produto nao serve.
/// </summary>
public sealed record ProductFitReason(
    string Criterion,
    decimal Points,
    decimal MaxPoints,
    bool Observed,
    string Rationale);

public sealed record ProductFit
{
    public required string Product { get; init; }
    public required IReadOnlyList<ProductFitReason> Reasons { get; init; }

    /// <summary>
    /// Verdadeiro para NO MAXIMO um produto da safra. A porta de entrada e uma
    /// so: um SDR que abre a conversa com tres produtos nao abre conversa
    /// nenhuma.
    /// </summary>
    public bool RecommendedEntry { get; init; }

    public decimal Score => Math.Round(
        Math.Clamp(Reasons.Sum(r => r.Points), 0m, 100m), 2);

    public decimal Coverage
    {
        get
        {
            var total = Reasons.Sum(r => r.MaxPoints);
            if (total == 0) return 0m;

            return Math.Round(Reasons.Where(r => r.Observed).Sum(r => r.MaxPoints) / total, 4);
        }
    }

    /// <summary>Personas do catalogo. O agente pode restringir; nao pode inventar.</summary>
    public IReadOnlyList<string> Personas =>
        ProductCatalog.Find(Product)?.Personas ?? [];
}

/// <summary>
/// Product Matcher (A04), metade deterministica.
///
/// Segue o ADR-0005 pela mesma razao que o Opportunity Score: "por que o
/// MotorHub caiu de 78 para 51?" precisa ter resposta, e uma nota gerada por
/// modelo nao tem. O que o modelo faz nesta etapa esta no
/// <see cref="Contracts.ProductPitchProfile"/>: escrever o argumento e antecipar
/// a objecao, com evidencia. A aritmetica e daqui.
///
/// Cada produto pontua de 0 a 100 sobre a dor QUE ELE resolve. Nao e uma
/// distribuicao - dois produtos podem pontuar alto ao mesmo tempo, e num grupo
/// com dez lojas e site ruim isso e o retrato correto. O que a plataforma
/// escolhe e a PORTA DE ENTRADA, e essa e unica.
/// </summary>
public static class ProductFitScoring
{
    public const string Version = "product-fit-v1";

    /// <summary>
    /// Piso para recomendar entrada. Abaixo disto a conta entra em nurture com o
    /// pedido de mais pesquisa, e nao com um produto escolhido a esmo entre
    /// cinco notas igualmente baixas.
    /// </summary>
    public const decimal EntryThreshold = 45m;

    /// <summary>
    /// Cobertura minima para a entrada valer. Um fit de 80 apurado sobre 30% dos
    /// criterios e um palpite com casas decimais: falta a auditoria, e o produto
    /// "certo" pode mudar quando ela chegar.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// <b>Com os pesos de hoje, este piso nunca decide nada</b> - e o registro
    /// disso importa mais do que o piso.
    /// </para>
    /// <para>
    /// A unica forma de a cobertura cair abaixo de 0,5 e faltar a auditoria, e
    /// sem ela a nota maxima alcancavel e 10 para FrontCar, 30 para AutoFollow e
    /// 40 para AutoTalk - todas abaixo de <see cref="EntryThreshold"/>. MotorHub
    /// (0,75) e BoxTech (0,65) tem cobertura alta mesmo sem auditoria, porque o
    /// que os sustenta - lojas, CNPJs, marcas, estoque - vem da pesquisa. O
    /// corte de nota chega primeiro em todos os cinco casos.
    /// </para>
    /// <para>
    /// Fica como rede contra REBALANCEAMENTO. No dia em que alguem mover peso
    /// para criterios que a pesquisa ja observa, um produto passa a alcancar 45
    /// sem nenhuma auditoria, e este piso volta a ser a unica coisa entre o
    /// diagnostico incompleto e uma abordagem construida sobre ele. Remove-lo
    /// por estar inativo hoje seria retirar a guarda exatamente antes da mudanca
    /// que a torna necessaria.
    /// </para>
    /// </remarks>
    public const decimal EntryMinimumCoverage = 0.5m;

    public static IReadOnlyList<ProductFit> Calculate(ProductFitInputs inputs)
    {
        var fits = new List<ProductFit>
        {
            new() { Product = ProductCatalog.FrontCar,   Reasons = [.. FrontCar(inputs)] },
            new() { Product = ProductCatalog.MotorHub,   Reasons = [.. MotorHub(inputs)] },
            new() { Product = ProductCatalog.AutoFollow, Reasons = [.. AutoFollow(inputs)] },
            new() { Product = ProductCatalog.AutoTalk,   Reasons = [.. AutoTalk(inputs)] },
            new() { Product = ProductCatalog.BoxTech,    Reasons = [.. BoxTech(inputs)] }
        };

        // A porta de entrada só pode ser um produto OFERTÁVEL hoje.
        //
        // Sem este filtro, a maior nota da safra abre a conversa mesmo quando
        // não existe o que vender — foi o que acontecia com o AutoTalk, que
        // vencia a entrada em conta grande sem estar pronto para oferta. A nota
        // dele continua sendo calculada e gravada de propósito: é o registro de
        // que a dor existia antes de haver produto, e é o que responde "quantas
        // contas esperavam por isso?" no dia em que ele existir.
        var entry = fits
            .Where(f => ProductCatalog.IsAvailable(f.Product))
            .Where(f => f.Score >= EntryThreshold && f.Coverage >= EntryMinimumCoverage)
            .OrderByDescending(f => f.Score)
            .ThenByDescending(f => f.Coverage)
            // Desempate estavel: sem ele, dois produtos empatados trocariam de
            // lugar entre duas execucoes identicas, e a "porta de entrada"
            // deixaria de ser reproduzivel - exatamente o que o ADR-0005 exige.
            .ThenBy(f => f.Product, StringComparer.Ordinal)
            .FirstOrDefault();

        return [.. fits.Select(f => f.Product == entry?.Product ? f with { RecommendedEntry = true } : f)];
    }

    // ------------------------------------------------------------- FrontCar
    // Dor: o site nao vende. Vitrine, desempenho, achabilidade e conversao.

    private static IEnumerable<ProductFitReason> FrontCar(ProductFitInputs i)
    {
        var a = i.Audit;

        // Site fora do ar e o caso mais forte que existe para FrontCar, e nao o
        // mais fraco. Tratar `Reachable=false` como "sem dado" mandaria a conta
        // com o pior site do funil para o fim da fila.
        if (a is { Reachable: false })
        {
            yield return new ProductFitReason(
                "site_fora_do_ar", 40m, 40m, true,
                "dominio nao respondeu: nao ha vitrine para o cliente encontrar");
        }
        else
        {
            yield return Pain("vitrine", a?.Inventory, 40m,
                "vitrine de veiculos", "sem auditoria de vitrine");
        }

        yield return Pain("desempenho", a?.Performance, 20m,
            "desempenho do site", "sem auditoria de desempenho");

        yield return Pain("achabilidade", a?.Seo, 15m,
            "SEO e landing pages", "sem auditoria de SEO");

        yield return Pain("conversao", a?.Conversion, 15m,
            "caminhos de conversao", "sem auditoria de conversao");

        // Replatform declarado vale mais que qualquer nota: quem ja decidiu
        // trocar de site tem orcamento e prazo, e a conversa muda de "voces tem
        // um problema" para "nos fazemos isso".
        yield return SignalReason(i, "replatform", 10m,
            ["replatform", "website_change", "migracao", "rebranding"],
            "site antigo ou replatform anunciado");
    }

    // ------------------------------------------------------------- MotorHub
    // Dor: o mesmo estoque mantido a mao em varios lugares.

    private static IEnumerable<ProductFitReason> MotorHub(ProductFitInputs i)
    {
        var stores = i.StoreCount;

        yield return new ProductFitReason(
            "unidades",
            stores switch { null => 0m, >= 10 => 30m, >= 5 => 25m, >= 3 => 18m, 2 => 10m, _ => 0m },
            30m, stores is not null,
            stores is null ? "contagem de lojas desconhecida" : $"{stores} unidade(s)");

        // Cada portal e uma vitrine a mais para manter coerente a mao. E o
        // sintoma mais direto do problema que o MotorHub resolve.
        var portals = i.Audit?.MultiplePortals;

        yield return new ProductFitReason(
            "canais_externos",
            portals switch
            {
                true => i.Audit?.PortalCount switch { >= 3 => 25m, _ => 18m },
                _ => 0m
            },
            25m, portals is not null,
            portals switch
            {
                true => $"estoque publicado em {i.Audit?.PortalCount?.ToString() ?? "mais de um"} canal(is) externo(s)",
                false => "estoque so no site proprio",
                _ => "canais externos nao verificados"
            });

        yield return new ProductFitReason(
            "grupo_economico",
            i.CnpjCount switch { >= 5 => 15m, >= 3 => 12m, 2 => 8m, _ => 0m },
            15m, true,
            $"{i.CnpjCount} CNPJ(s) na conta");

        var inventory = i.InventoryEstimate;

        yield return new ProductFitReason(
            "volume_de_estoque",
            inventory switch { null => 0m, >= 500 => 15m, >= 200 => 12m, >= 80 => 8m, >= 30 => 4m, _ => 0m },
            15m, inventory is not null,
            inventory is null ? "estoque nao estimado" : $"~{inventory} veiculo(s)");

        yield return new ProductFitReason(
            "marcas",
            i.BrandCount switch { >= 4 => 15m, >= 2 => 10m, _ => 0m },
            15m, i.BrandCount > 0 || i.HasAuthorizedBrand,
            i.BrandCount > 0 ? $"{i.BrandCount} marca(s) na operacao" : "marcas nao levantadas");
    }

    // ----------------------------------------------------------- AutoFollow
    // Dor: o lead chega e ninguem sabe o que houve com ele.

    private static IEnumerable<ProductFitReason> AutoFollow(ProductFitInputs i)
    {
        var a = i.Audit;
        var hasCrm = HasCategory(i, TechnologyCategory.Crm);
        var auditRan = a is { Reachable: true };

        // O achado que define o produto: o site CAPTURA lead e nao ha CRM a
        // vista. Sem captura, nao ha follow-up a fazer, e a dor e outra.
        //
        // Vale 35 e nao 30 porque os outros quatro criterios somam 60: com 30
        // aqui o AutoFollow tinha teto 95 enquanto FrontCar, MotorHub, AutoTalk
        // e BoxTech chegavam a 100. Como `RecommendedEntry` sai de um
        // `OrderByDescending(Score)` entre os cinco, isso era um desconto de 5
        // pontos na disputa pela porta de entrada - estrutural, silencioso e sem
        // nada no diagnostico que o justificasse. O peso que faltava foi para o
        // criterio que DEFINE o produto, no mesmo patamar do `atrito_de_contato`
        // do AutoTalk.
        var capturesLead = a?.Conversion is > 0.2m;

        yield return new ProductFitReason(
            "captura_sem_destino",
            (capturesLead, hasCrm) switch
            {
                (true, false) => 35m,
                (true, true) => 10m,
                _ => 0m
            },
            35m, auditRan,
            !auditRan ? "sem auditoria de captura de lead"
                : (capturesLead, hasCrm) switch
                {
                    (true, false) => "site captura lead e nenhum CRM foi detectado",
                    (true, true) => "site captura lead e ja existe CRM",
                    _ => "site nao aparenta capturar lead"
                });

        // Sem analytics nem tag manager ninguem sabe quantos leads entraram - o
        // que faz o follow-up ser discutido por impressao em vez de por numero.
        var measures = HasCategory(i, TechnologyCategory.Analytics) ||
                       HasCategory(i, TechnologyCategory.TagManager);

        // Ausencia de assinatura pesa MENOS que medicao, e essa e a regra que
        // vale para os tres criterios de ausencia deste arquivo. A sonda so
        // enxerga assinatura que ela conhece: "nenhum analytics detectado" pode
        // ser um GA4 carregado por um gerenciador de tags que o catalogo nao
        // tem. Medicao erra por ruido; ausencia erra por ignorancia, e a
        // segunda merece menos ponto.
        yield return new ProductFitReason(
            "medicao",
            auditRan && !measures ? 15m : 0m,
            15m, auditRan,
            !auditRan ? "medicao nao verificada"
                : measures ? "analytics ou tag manager presentes" : "nenhuma ferramenta de medicao detectada");

        var stores = i.StoreCount;

        yield return new ProductFitReason(
            "volume_comercial",
            stores switch { null => 0m, >= 5 => 25m, >= 3 => 18m, 2 => 12m, 1 => 6m, _ => 0m },
            25m, stores is not null,
            stores is null ? "porte comercial desconhecido" : $"{stores} ponto(s) de venda");

        yield return new ProductFitReason(
            "trafego_pago",
            HasCategory(i, TechnologyCategory.Ads) ? 15m : 0m,
            15m, auditRan,
            !auditRan ? "midia paga nao verificada"
                : HasCategory(i, TechnologyCategory.Ads)
                    ? "investe em midia paga: cada lead perdido tem custo conhecido"
                    : "sem indicio de midia paga");

        yield return SignalReason(i, "contratacao_comercial", 10m,
            ["job_posting", "hiring", "vaga"],
            "vaga comercial ou de BDC anunciada");
    }

    // ------------------------------------------------------------- AutoTalk
    // Dor: o cliente quer conversar e nao tem por onde.

    private static IEnumerable<ProductFitReason> AutoTalk(ProductFitInputs i)
    {
        var a = i.Audit;
        var auditRan = a is { Reachable: true };
        var hasChat = HasCategory(i, TechnologyCategory.Chat);

        // Corroborado pela conversao medida, e nao so pela ausencia de
        // assinatura. Sem a corroboracao, qualquer site cujo widget de
        // atendimento a sonda nao reconheca - um botao de WhatsApp caseiro, um
        // chat proprio - pontuaria o maximo, e AutoTalk venceria a porta de
        // entrada em toda conta grande, independentemente de haver dor de
        // atendimento.
        var conversion = a?.Conversion;

        yield return new ProductFitReason(
            "canal_de_conversa",
            (auditRan, hasChat, conversion) switch
            {
                (true, false, { } c) when c < 0.5m => 30m,
                (true, false, _) => 12m,
                _ => 0m
            },
            30m, auditRan,
            !auditRan ? "canais de conversa nao verificados"
                : hasChat ? "ja existe chat ou atendimento no site"
                : conversion is { } conv && conv < 0.5m
                    ? $"nenhum chat detectado e conversao em {conv:P0}"
                    : "nenhum chat detectado, mas a conversao nao confirma atrito");

        // Conversao baixa com vitrine razoavel costuma ser atrito de contato, e
        // nao de catalogo: o carro esta la, o caminho para perguntar sobre ele
        // e que nao esta.
        //
        // Proporcional e nao em degrau: o degrau fazia conversao 0,49 e 0,51
        // valerem 25 e 0 pontos, e uma diferenca de um centesimo na medicao
        // trocava a porta de entrada da conta.
        var inventory = a?.Inventory;

        var atrito = (conversion, inventory) switch
        {
            ({ } c, { } shelf) when shelf >= 0.5m => Math.Round((1m - Math.Clamp(c, 0m, 1m)) * 35m, 2),
            ({ } c, _) => Math.Round((1m - Math.Clamp(c, 0m, 1m)) * 25m, 2),
            _ => 0m
        };

        yield return new ProductFitReason(
            "atrito_de_contato", atrito, 35m, conversion is not null,
            conversion is null ? "conversao nao auditada"
                : $"conversao em {conversion:P0} com vitrine em {(inventory is { } v ? v.ToString("P0") : "n/d")}");

        var stores = i.StoreCount;

        yield return new ProductFitReason(
            "atendimento_distribuido",
            stores switch { null => 0m, >= 5 => 20m, >= 3 => 15m, 2 => 8m, _ => 0m },
            20m, stores is not null,
            stores is null ? "distribuicao do atendimento desconhecida" : $"atendimento em {stores} unidade(s)");

        yield return new ProductFitReason(
            "volume_de_demanda",
            i.InventoryEstimate switch { null => 0m, >= 200 => 15m, >= 80 => 10m, >= 30 => 6m, _ => 0m },
            15m, i.InventoryEstimate is not null,
            i.InventoryEstimate is null ? "estoque nao estimado" : $"~{i.InventoryEstimate} veiculo(s) gerando duvida");
    }

    // -------------------------------------------------------------- BoxTech
    // Dor: a operacao passou do ponto em que ferramentas soltas dao conta.

    private static IEnumerable<ProductFitReason> BoxTech(ProductFitInputs i)
    {
        var stores = i.StoreCount;

        yield return new ProductFitReason(
            "porte",
            stores switch { null => 0m, >= 10 => 30m, >= 5 => 22m, >= 3 => 12m, _ => 0m },
            30m, stores is not null,
            stores is null ? "porte desconhecido" : $"{stores} unidade(s)");

        yield return new ProductFitReason(
            "grupo_economico",
            i.CnpjCount switch { >= 5 => 20m, >= 3 => 15m, 2 => 6m, _ => 0m },
            20m, true,
            $"{i.CnpjCount} CNPJ(s) na conta");

        var complex = i.Audit?.ComplexIntegration;

        yield return new ProductFitReason(
            "heterogeneidade",
            complex switch { true => 20m, _ => 0m },
            20m, complex is not null,
            complex switch
            {
                true => "integracoes heterogeneas aparentes",
                false => "pilha aparentemente homogenea",
                _ => "integracoes nao verificadas"
            });

        // Rede autorizada traz exigencia de fabricante - layout, prazo de
        // publicacao, integracao com o DMS da marca - e e a diferenca entre uma
        // plataforma e um site bonito.
        yield return new ProductFitReason(
            "rede_autorizada",
            i.HasAuthorizedBrand ? 15m : i.BrandCount switch { >= 3 => 8m, _ => 0m },
            15m, i.BrandCount > 0 || i.HasAuthorizedBrand,
            i.HasAuthorizedBrand ? "concessionaria autorizada" : $"{i.BrandCount} marca(s)");

        var dms = HasCategory(i, TechnologyCategory.Dms);

        yield return new ProductFitReason(
            "dms",
            dms ? 15m : 0m,
            15m, i.Audit is { Reachable: true },
            i.Audit is { Reachable: true }
                ? dms ? "DMS identificado: integracao e pre-requisito da conversa" : "nenhum DMS identificado"
                : "DMS nao verificado");
    }

    // ------------------------------------------------------------ auxiliares

    /// <summary>
    /// Converte uma nota de auditoria em dor: quanto pior a nota, mais pontos. A
    /// ausencia de nota nao vira zero de dor - vira criterio nao observado, para
    /// que <see cref="ProductFit.Coverage"/> denuncie o que falta em vez de o
    /// score fingir que o site e bom.
    /// </summary>
    private static ProductFitReason Pain(
        string criterion, decimal? score, decimal max, string label, string missingLabel) =>
        new(criterion,
            score is null ? 0m : Math.Round((1m - Math.Clamp(score.Value, 0m, 1m)) * max, 2),
            max, score is not null,
            score is null ? missingLabel : $"{label} em {score:P0}");

    private static ProductFitReason SignalReason(
        ProductFitInputs i, string criterion, decimal max, string[] match, string label)
    {
        var best = i.Signals
            .Where(s => match.Any(m => s.SignalType.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new { Signal = s, Value = max * Math.Clamp(s.Strength, 0m, 1m) * Recency(s.ObservedAt, i.ReferenceDate) })
            .OrderByDescending(x => x.Value)
            .FirstOrDefault();

        return new ProductFitReason(
            criterion,
            best is null ? 0m : Math.Round(best.Value, 2),
            max, i.Signals.Count > 0,
            best is null
                ? $"sem sinal de {label}"
                : $"{label} (forca {best.Signal.Strength:0.00}, em {best.Signal.ObservedAt:yyyy-MM-dd})");
    }

    /// <summary>
    /// Mesma curva do <see cref="OpportunityScoring"/>: peso cheio ate 90 dias,
    /// decaimento linear ate zerar em um ano. Duas curvas diferentes fariam o
    /// mesmo sinal valer coisas distintas em duas telas do mesmo painel.
    /// </summary>
    private static decimal Recency(DateTimeOffset observedAt, DateTimeOffset reference)
    {
        var age = reference - observedAt;

        if (age <= TimeSpan.FromDays(90)) return 1m;
        if (age >= TimeSpan.FromDays(365)) return 0m;

        return Math.Clamp((decimal)((TimeSpan.FromDays(365) - age).TotalDays / 275.0), 0m, 1m);
    }

    private static bool HasCategory(ProductFitInputs i, string category) =>
        i.Technologies.Any(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase));
}
