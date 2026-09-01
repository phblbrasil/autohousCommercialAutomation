using System.Text;
using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Monta o prompt do Website Auditor a partir do arquivo versionado, do schema,
/// do contexto da conta e da MEDICAO da sonda.
///
/// A medicao nao entra como JSON cru no prompt, e sim como um quadro em texto.
/// Nao e estetica: um objeto com dezoito campos anulaveis convida o modelo a
/// tratar `null` como `false` - "HasSitemap: null" vira "nao tem sitemap" - e
/// entao ele reporta como achado uma coisa que a sonda nao conseguiu verificar.
/// O quadro escreve "nao verificado" por extenso, que e o unico jeito de a
/// distincao sobreviver a leitura.
/// </summary>
public sealed class WebsiteAuditPromptBuilder : IWebsiteAuditPromptBuilder
{
    public const string PromptVersionValue = "website-auditor-v1";
    public const string AgentNameValue = "website-auditor";

    public string PromptVersion => PromptVersionValue;
    public string AgentName => AgentNameValue;

    private readonly string _template;
    private readonly string _schema;

    public WebsiteAuditPromptBuilder(string promptPath, string schemaPath)
    {
        if (!File.Exists(promptPath))
            throw new FileNotFoundException($"Prompt do Website Auditor nao encontrado: {promptPath}");

        if (!File.Exists(schemaPath))
            throw new FileNotFoundException($"Schema da auditoria nao encontrado: {schemaPath}");

        _template = File.ReadAllText(promptPath);
        _schema = File.ReadAllText(schemaPath);
    }

    public string BuildSystemPrompt() =>
        _template
            .Replace("{{OUTPUT_SCHEMA}}", _schema)
            .Replace("{{ACCOUNT_CONTEXT}}", "(fornecido na mensagem do usuario)");

    public string BuildUserPrompt(AccountContext context, WebsiteProbeResult probe) =>
        $"""
        Audite o site desta conta e devolva o Website Audit em JSON.

        ## Conta

        {Serialize(context)}

        ## Medicao ja feita pela plataforma

        Estes numeros JA ESTAO REGISTRADOS. Nao os repita, nao os estime e nao os
        contradiga - use-os como contexto do que voce vai observar na pagina.

        {Describe(probe)}
        """;

    public string BuildRepairPrompt(
        AccountContext context, WebsiteProbeResult probe, string previousOutput, string violations) =>
        $"""
        A resposta anterior nao satisfez o contrato e foi rejeitada.

        Violacoes encontradas:
        {violations}

        Corrija e devolva APENAS o objeto JSON valido, sem texto ao redor.

        Resposta anterior:
        {Truncate(previousOutput)}

        ## Conta

        {Serialize(context)}

        ## Medicao ja feita pela plataforma

        {Describe(probe)}
        """;

    /// <summary>
    /// Quadro legivel da medicao. Cada linha diz uma de tres coisas - o valor,
    /// "nao" ou "nao verificado" - e nunca deixa o modelo inferir qual das duas
    /// ultimas e o caso.
    /// </summary>
    internal static string Describe(WebsiteProbeResult p)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"URL pedida .............. {p.RequestedUrl}");

        if (!string.IsNullOrWhiteSpace(p.FinalUrl) && p.FinalUrl != p.RequestedUrl)
            sb.AppendLine($"URL final (redirect) .... {p.FinalUrl}");

        sb.AppendLine($"Status HTTP ............. {Show(p.StatusCode)}");

        if (!string.IsNullOrWhiteSpace(p.Error))
            sb.AppendLine($"Erro .................... {p.Error}");

        sb.AppendLine($"Tempo ate 1o byte ....... {Ms(p.TimeToFirstByte)}");
        sb.AppendLine($"Tempo do documento ...... {Ms(p.DocumentLoadTime)}");
        sb.AppendLine($"Peso do HTML ............ {Bytes(p.DocumentBytes)}");
        sb.AppendLine($"Recursos bloqueantes .... {Show(p.RenderBlockingResources)}");
        sb.AppendLine($"Compressao .............. {Flag(p.CompressionEnabled)}");
        sb.AppendLine($"HTTPS ................... {Flag(p.IsHttps)}");
        sb.AppendLine($"<title> ................. {Flag(p.HasTitle)}");
        sb.AppendLine($"meta description ........ {Flag(p.HasMetaDescription)}");
        sb.AppendLine($"<h1> .................... {Flag(p.HasH1)}");
        sb.AppendLine($"canonical ............... {Flag(p.HasCanonical)}");
        sb.AppendLine($"dados estruturados ...... {Flag(p.HasStructuredData)}");
        sb.AppendLine($"sitemap.xml ............. {Flag(p.HasSitemap)}");
        sb.AppendLine($"robots.txt .............. {Flag(p.HasRobotsTxt)}");
        sb.AppendLine($"meta viewport ........... {Flag(p.HasViewportMeta)}");
        sb.AppendLine($"viewport largura fixa ... {Flag(p.HasFixedWidthViewport)}");

        sb.AppendLine();
        sb.AppendLine("Tecnologias detectadas no HTML:");

        if (p.Technologies.Count == 0)
        {
            sb.AppendLine("  (nenhuma assinatura conhecida encontrada)");
        }
        else
        {
            foreach (var tech in p.Technologies)
            {
                sb.AppendLine($"  - [{tech.Category}] {tech.Name}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Show(int? value) => value?.ToString() ?? "nao verificado";

    private static string Ms(TimeSpan? value) =>
        value is { } v ? $"{v.TotalMilliseconds:0} ms" : "nao verificado";

    private static string Bytes(long? value) =>
        value is { } v ? $"{v / 1024.0:0.#} KB" : "nao verificado";

    /// <summary>
    /// Tres estados em palavras. `false` e `null` viram textos distintos porque a
    /// diferenca entre "o site nao tem sitemap" e "nao consegui checar o sitemap"
    /// e a diferenca entre um achado e uma invencao.
    /// </summary>
    private static string Flag(bool? value) => value switch
    {
        true => "sim",
        false => "NAO",
        null => "nao verificado"
    };

    private static string Serialize(AccountContext context) =>
        JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });

    private static string Truncate(string text, int max = 6000) =>
        text.Length <= max ? text : text[..max] + "\n...(truncado)";
}
