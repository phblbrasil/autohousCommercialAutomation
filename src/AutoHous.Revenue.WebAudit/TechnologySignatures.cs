using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.WebAudit;

/// <summary>
/// Assinaturas de tecnologia no HTML.
///
/// Cada deteccao guarda o TRECHO que a produziu. Isso e deliberado e nao
/// diagnostico: <c>technologies</c> distingue <c>source='probe'</c> de
/// <c>source='agent'</c>, e a migration 0015 exige evidencia para a segunda. A
/// medicao dispensa evidencia porque ela propria e a fonte - mas so se der para
/// mostrar o que casou. "Detectamos Google Analytics" sem o trecho e uma
/// afirmacao sem lastro, ainda que quem a tenha feito seja uma regex.
///
/// A lista e curta de proposito. Sao as ferramentas que aparecem de fato em site
/// de concessionaria e revenda no Brasil, e cada entrada foi escolhida por
/// responder a uma pergunta comercial:
///
///   analytics / tag_manager   a empresa mede alguma coisa?
///   ads                       ela paga por trafego que talvez nao saiba medir?
///   chat / crm                por onde o lead entra, e para onde ele vai?
///   inventory / marketplace   quem publica o estoque, e em quantos lugares?
///
/// Uma lista maior parece melhor e nao e: assinatura que ninguem sabe
/// interpretar vira ruido em `complex_integration`, que conta CATEGORIAS
/// distintas e portanto sofre com falso positivo.
/// </summary>
public static class TechnologySignatures
{
    private sealed record Signature(string Category, string Name, string[] Needles);

    private static readonly Signature[] All =
    [
        // -------------------------------------------------------- medicao
        new(TechnologyCategory.Analytics, "Google Analytics 4",
            ["gtag/js?id=G-", "googletagmanager.com/gtag/js"]),
        new(TechnologyCategory.Analytics, "Google Analytics (Universal)",
            ["google-analytics.com/analytics.js", "ga('create'"]),
        new(TechnologyCategory.TagManager, "Google Tag Manager",
            ["googletagmanager.com/gtm.js", "GTM-"]),
        new(TechnologyCategory.Analytics, "Hotjar", ["static.hotjar.com"]),
        new(TechnologyCategory.Analytics, "Clarity", ["clarity.ms/tag"]),

        // ----------------------------------------------------------- ads
        new(TechnologyCategory.Ads, "Meta Pixel",
            ["connect.facebook.net", "fbq('init'"]),
        new(TechnologyCategory.Ads, "Google Ads", ["googleadservices.com", "gtag('config', 'AW-"]),
        new(TechnologyCategory.Ads, "TikTok Pixel", ["analytics.tiktok.com"]),

        // ---------------------------------------------------------- chat
        new(TechnologyCategory.Chat, "WhatsApp", ["wa.me/", "api.whatsapp.com/send"]),
        new(TechnologyCategory.Chat, "JivoChat", ["code.jivosite.com"]),
        new(TechnologyCategory.Chat, "Tawk.to", ["embed.tawk.to"]),
        new(TechnologyCategory.Chat, "Zendesk Chat", ["static.zdassets.com"]),

        // ----------------------------------------------------------- crm
        new(TechnologyCategory.Crm, "RD Station", ["d335luupugsy2.cloudfront.net", "rdstation"]),
        new(TechnologyCategory.Crm, "HubSpot", ["js.hs-scripts.com", "hs-analytics.net"]),
        new(TechnologyCategory.Crm, "Salesforce", ["salesforce.com", "pardot.com"]),

        // ---------------------------------------- plataformas do setor
        // As que publicam ou sindicalizam estoque. Sao as que mais importam:
        // duas delas no mesmo site e o sintoma direto de fragmentacao.
        new(TechnologyCategory.InventoryPlatform, "Autoforce", ["autoforce.com"]),
        new(TechnologyCategory.InventoryPlatform, "Boom Sistemas", ["boomsistemas.com.br"]),
        new(TechnologyCategory.InventoryPlatform, "Syonet", ["syonet.com"]),
        new(TechnologyCategory.InventoryPlatform, "Dealernet", ["dealernet.com.br"]),
        new(TechnologyCategory.Marketplace, "Webmotors", ["webmotors.com.br"]),
        new(TechnologyCategory.Marketplace, "iCarros", ["icarros.com.br"]),
        new(TechnologyCategory.Marketplace, "OLX Autos", ["olx.com.br"]),
        new(TechnologyCategory.Marketplace, "Mobiauto", ["mobiauto.com.br"]),
        new(TechnologyCategory.Marketplace, "Usadosbr", ["usadosbr.com"]),

        // ----------------------------------------------------------- cms
        new(TechnologyCategory.Cms, "WordPress", ["/wp-content/", "/wp-includes/"]),
        new(TechnologyCategory.Cms, "Wix", ["static.parastorage.com"]),
        new(TechnologyCategory.Ecommerce, "VTEX", ["vtexassets.com", "vteximg.com.br"]),
        new(TechnologyCategory.Ecommerce, "Shopify", ["cdn.shopify.com"])
    ];

    public static IReadOnlyList<DetectedTechnology> DetectAll(string html)
    {
        if (string.IsNullOrEmpty(html)) return [];

        var found = new List<DetectedTechnology>();

        foreach (var signature in All)
        {
            var match = signature.Needles.FirstOrDefault(
                n => html.Contains(n, StringComparison.OrdinalIgnoreCase));

            if (match is null) continue;

            found.Add(new DetectedTechnology
            {
                Category = signature.Category,
                Name = signature.Name,
                Match = match,

                // Deteccao por substring nao e certeza. Um link para o proprio
                // perfil no Webmotors casa com a assinatura do Webmotors sem que
                // o estoque esteja sindicalizado la. 0.9 registra que a medicao e
                // forte, e nao infalivel - e deixa o campo pronto para uma
                // assinatura mais especifica baixar ou subir isso depois.
                Confidence = 0.9m
            });
        }

        return found;
    }
}
