using System.Security.Cryptography;
using System.Text;

namespace AutoHous.Revenue.Api;

/// <summary>
/// Credencial de borda da Revenue API.
///
/// Ate aqui a API era aberta: o MCP mandava <c>Bearer</c> e nada validava do
/// outro lado. Em dev isso passa despercebido; em HML/PRD e uma superficie de
/// escrita (<c>POST /accounts</c>, <c>/research</c>, decisao de merge) sem dono.
///
/// Tres decisoes que so fazem sentido explicadas:
///
/// **Falha fechada na inicializacao.** Sem chave utilizavel o processo nao sobe.
/// Subir sem credencial e o pior dos mundos: parece saudavel, responde 200 e so
/// se descobre o buraco quando alguem de fora encontra. E o mesmo criterio que o
/// gateway do Hermes aplica a si mesmo (<c>API_SERVER_KEY</c>), o que mantem a
/// regra unica dos dois lados da integracao.
///
/// **Lista de chaves, nao chave unica.** Rotacionar credencial em PRD sem
/// derrubar o consumidor exige um intervalo em que a antiga e a nova valem.
/// <c>REVENUE_API_KEY=nova,antiga</c> cobre a janela; depois some com a antiga.
///
/// **Comparacao em tempo fixo sobre o digest.** Comparar string com <c>==</c>
/// sai no primeiro byte diferente, e a diferenca de tempo entre "errou no
/// primeiro caractere" e "errou no ultimo" e mensuravel pela rede. SHA-256
/// iguala o tamanho e <see cref="CryptographicOperations.FixedTimeEquals"/>
/// iguala o tempo.
/// </summary>
public sealed class RevenueApiKeys
{
    public const string ConfigurationKey = "REVENUE_API_KEY";

    /// <summary>
    /// Caminho de arquivo com a chave, e preferido sobre a variavel direta.
    ///
    /// E o formato que Docker secrets e Kubernetes montam: um arquivo por
    /// segredo, com permissao propria. Variavel de ambiente vaza no
    /// <c>docker inspect</c>, no <c>/proc/{pid}/environ</c> e em qualquer dump
    /// de processo; arquivo com 0600 nao.
    /// </summary>
    public const string FileConfigurationKey = "REVENUE_API_KEY_FILE";

    /// <summary>
    /// Piso de tamanho. 24 caracteres cobrem com folga uma chave gerada por
    /// <c>openssl rand -hex</c> e derrubam qualquer senha digitada a mao.
    /// </summary>
    public const int MinimumKeyLength = 24;

    private static readonly string[] Placeholders =
    [
        "changeme", "change_me", "change-me", "coloque-a-chave", "sua-chave",
        "secret", "password", "senha", "revenue", "autohous", "todo", "xxx"
    ];

    private readonly byte[][] _digests;

    private RevenueApiKeys(byte[][] digests) => _digests = digests;

    /// <summary>Quantas chaves estao ativas. Mais de uma significa rotacao em curso.</summary>
    public int Count => _digests.Length;

    /// <summary>
    /// Le a configuracao e falha se nao houver chave utilizavel.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nenhuma chave, ou todas curtas/placeholder. A mensagem diz o que fazer -
    /// um erro de configuracao na subida e lido por quem esta operando, nao por
    /// quem escreveu o codigo.
    /// </exception>
    public static RevenueApiKeys Load(IConfiguration configuration)
    {
        var raw = ReadFromFile(configuration[FileConfigurationKey])
                  ?? configuration[ConfigurationKey];

        var candidates = (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var usable = candidates.Where(IsUsable).ToArray();

        if (usable.Length == 0)
        {
            throw new InvalidOperationException(
                $"""
                 {ConfigurationKey} nao configurada ou fraca demais. A Revenue API
                 nao sobe sem credencial - ela expoe escrita de conta, pesquisa e
                 decisao de merge.

                 Gere e exporte:

                     export {ConfigurationKey}=$(openssl rand -hex 24)

                 Ou aponte para um arquivo (Docker secret, volume do Kubernetes):

                     export {FileConfigurationKey}=/run/secrets/revenue-api-key

                 Minimo de {MinimumKeyLength} caracteres. Varias chaves separadas por
                 virgula convivem, para rotacionar sem derrubar o consumidor.
                 """);
        }

        return new RevenueApiKeys([.. usable.Select(Digest)]);
    }

    /// <summary>Confere a chave apresentada contra todas as ativas, em tempo fixo.</summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return false;

        var candidate = Digest(presented);
        var matched = false;

        // Sem short-circuit de proposito: sair no primeiro acerto contaria
        // quantas chaves existem antes da que casou.
        foreach (var digest in _digests)
        {
            matched |= CryptographicOperations.FixedTimeEquals(digest, candidate);
        }

        return matched;
    }

    private static bool IsUsable(string key) =>
        key.Length >= MinimumKeyLength
        && !Placeholders.Contains(key.ToLowerInvariant());

    private static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static string? ReadFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{FileConfigurationKey} aponta para '{path}', que nao existe. " +
                "Em container isso costuma ser secret nao montado - a API para aqui " +
                "em vez de subir sem credencial.");
        }

        return File.ReadAllText(path).Trim();
    }
}
