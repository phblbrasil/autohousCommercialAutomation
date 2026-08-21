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
        // Catalogo estatico: nao depende do estado do banco e nao justifica uma
        // chamada de rede. Sai daqui quando houver tabela de produtos.
        var catalog = new[]
        {
            new { product = "FrontCar",        solves = "Site, vitrine de estoque, ofertas e landing pages", personas = new[] { "Diretor de Marketing", "Gerente de Marketing", "Diretor Comercial", "Head Digital", "Socio" } },
            new { product = "MotorHub",        solves = "Integracao e distribuicao de estoque entre unidades e canais", personas = new[] { "CTO", "CIO", "Head de TI", "Gerente de Sistemas", "Diretor de Operacoes" } },
            new { product = "AutoFollow",      solves = "Follow-up e gestao de leads comerciais", personas = new[] { "Diretor Comercial", "Gerente Comercial", "CRM Manager", "BDC Manager" } },
            new { product = "AutoTalk",        solves = "Atendimento e conversacao com o cliente", personas = new[] { "Diretor Comercial", "CX", "Operacoes", "Atendimento" } },
            new { product = "BoxTech",         solves = "Plataforma tecnologica para operacoes maiores", personas = new[] { "CIO", "CTO", "Head de Digital", "Diretor de Tecnologia" } },
            new { product = "Partner Program", solves = "Canal via agencias e integradores", personas = new[] { "Socio de agencia", "Head de Novos Negocios" } }
        };

        return Task.FromResult(JsonSerializer.Serialize(catalog));
    }
}
