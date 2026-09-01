using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Mede um site. A metade determinista do Website Auditor (A03).
///
/// Porta, e nao classe, porque o caso de uso precisa da capacidade "medir um
/// site" e nao da implementacao. Isso e o que permite a troca que a analise de
/// lacunas antecipou: hoje a implementacao e HTTP puro
/// (<c>AutoHous.Revenue.WebAudit.HttpWebsiteProbe</c>), amanha um headless
/// browser entra atras da MESMA porta, e o caso de uso, o scoring e a
/// persistencia nao mudam uma linha.
///
/// A ordem foi escolhida assim de proposito. A gap-analysis registrou o headless
/// browser como "a maior peca de infraestrutura nova" do A03; deixar o auditor
/// inteiro esperando por ela seria travar tres entregas (technologies, Technology
/// Pain de verdade, e o A04 Product Matcher que depende do audit) em nome de
/// medidas que so um browser da - Core Web Vitals, layout shift, JavaScript
/// renderizado. O que a sonda HTTP mede ja sustenta quatro das sete notas.
///
/// **O que a sonda HTTP nao ve, e o browser veria:** conteudo que so existe
/// depois do JavaScript rodar. Numa vitrine em SPA, isso e o estoque inteiro.
/// Por isso <see cref="WebsiteProbeResult"/> distingue nulo de zero em todo
/// campo, e por isso a contagem de veiculos e pergunta para o AGENTE, que navega
/// de verdade, e nunca para a sonda.
/// </summary>
public interface IWebsiteProbe
{
    /// <summary>Nome da sonda, para <c>website_audits.probe</c> e log.</summary>
    string Name { get; }

    /// <summary>
    /// Nunca lanca por site fora do ar: um dominio morto e um RESULTADO da
    /// auditoria - e um sinal comercial forte -, nao um erro de execucao. Ver
    /// <see cref="WebsiteProbeResult.Unreachable"/>.
    /// </summary>
    Task<WebsiteProbeResult> ProbeAsync(string url, CancellationToken ct = default);
}
