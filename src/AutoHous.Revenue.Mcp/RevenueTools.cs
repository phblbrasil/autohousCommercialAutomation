using AutoHous.Revenue.Domain;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AutoHous.Revenue.Mcp;

/// <summary>
/// Superficie exposta ao Hermes. SOMENTE LEITURA, deliberadamente.
///
/// Nesta entrega o agente nao escreve via MCP: a persistencia sai do output
/// validado, dentro de uma transacao. Ferramentas de escrita quebrariam
/// atomicidade e idempotencia - entram quando houver fluxo interativo que as
/// justifique.
///
/// Nada de execute_sql_arbitrary, raw_db_shell ou send_any_message (ADR-003).
/// </summary>
[McpServerToolType]
public sealed class RevenueTools(RevenueApiClient api)
{
    [McpServerTool(Name = "get_account_context")]
    [Description("Retorna o contexto comercial de uma conta: nome, dominio, segmento, cidade, CNPJs, marcas conhecidas e quantidade de evidencias ja registradas.")]
    public async Task<string> GetAccountContextAsync(
        [Description("Identificador UUID da conta.")] string accountId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(accountId, out var id))
        {
            return JsonSerializer.Serialize(new { error = "invalid_account_id", value = accountId });
        }

        return await api.GetJsonAsync($"/accounts/{id}/context", ct);
    }

    [McpServerTool(Name = "list_account_evidence")]
    [Description("Lista as evidencias ja registradas para uma conta, cada uma com a URL da fonte. Util para nao repesquisar o que ja esta lastreado.")]
    public async Task<string> ListAccountEvidenceAsync(
        [Description("Identificador UUID da conta.")] string accountId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(accountId, out var id))
        {
            return JsonSerializer.Serialize(new { error = "invalid_account_id", value = accountId });
        }

        return await api.GetJsonAsync($"/accounts/{id}/evidence", ct);
    }

    [McpServerTool(Name = "get_product_catalog")]
    [Description("Retorna o catalogo de produtos da AutoHous com o problema que cada um resolve e as personas-alvo.")]
    public Task<string> GetProductCatalogAsync(CancellationToken ct = default)
    {
        // Le do DOMINIO, e nao de uma lista propria.
        //
        // Ate o Product Matcher (A04) existir, a lista morava aqui e nao havia
        // problema: nada mais no sistema precisava saber quais eram os produtos.
        // Agora ProductFitScoring calcula o fit por produto e o
        // EvidenceFirstGuard recusa persona fora do catalogo daquele produto - e
        // duas listas significariam o agente enxergando um catalogo e a
        // plataforma validando contra outro.
        //
        // O sintoma dessa divergencia seria dos piores: um pitch bem escrito,
        // com a persona que a ferramenta MCP anunciou, rejeitado pelo guard com
        // "nao e persona deste produto". O erro apareceria como falha do modelo.
        //
        // Estatico ainda: nao depende do estado do banco e nao justifica uma
        // chamada de rede. Sai daqui quando houver tabela de produtos.
        var catalog = ProductCatalog.All.Select(p => new
        {
            product = p.Name,
            solves = p.Solves,
            personas = p.Personas
        });

        return Task.FromResult(JsonSerializer.Serialize(catalog));
    }
}
