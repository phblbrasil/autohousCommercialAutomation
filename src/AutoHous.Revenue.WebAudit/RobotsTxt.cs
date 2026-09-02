using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.WebAudit;

/// <summary>
/// Leitura de <c>robots.txt</c> só para a pergunta que a auditoria faz: quais
/// rastreadores de IA estão bloqueados da raiz do site.
///
/// Não é um parser completo da especificação, e não precisa ser. O que interessa
/// é a regra que decide se o agente consegue ler a home — <c>Disallow: /</c> —
/// e não a árvore inteira de permissões por caminho.
///
/// Duas regras da especificação que **não** dá para ignorar, porque errar
/// qualquer uma inverte o diagnóstico:
///
/// 1. **Um grupo pode ter vários <c>User-agent</c>.** Linhas de agente
///    consecutivas formam um grupo só, e as diretivas abaixo valem para todos.
///    Ler par a par faria `GPTBot` herdar a regra do bloco seguinte.
/// 2. **O grupo específico vence o <c>*</c>.** Um site com
///    <c>Disallow: /</c> em <c>*</c> e um grupo <c>GPTBot</c> com
///    <c>Allow: /</c> **libera** o GPTBot. Considerar só o coringa acusaria
///    bloqueio onde há permissão explícita.
/// </summary>
public static class RobotsTxt
{
    /// <summary>
    /// Agentes de IA cuja leitura da raiz está bloqueada. Vazio quer dizer
    /// "nenhum"; a ausência do arquivo é tratada pelo chamador, e é diferente.
    /// </summary>
    public static IReadOnlyList<string> BlockedAiCrawlers(string content)
    {
        var groups = Parse(content);

        var wildcard = groups.FirstOrDefault(g => g.Agents.Contains("*"));

        return
        [
            .. AiCrawlers.All
                .Where(c => IsBlocked(c.UserAgent, groups, wildcard))
                .Select(c => c.UserAgent)
        ];
    }

    private static bool IsBlocked(string userAgent, List<Group> groups, Group? wildcard)
    {
        // Regra 2: o grupo do próprio agente vence o coringa, inclusive para
        // LIBERAR o que o coringa bloqueia.
        var own = groups.FirstOrDefault(g =>
            g.Agents.Any(a => string.Equals(a, userAgent, StringComparison.OrdinalIgnoreCase)));

        return (own ?? wildcard)?.BlocksRoot ?? false;
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<string> Disallow { get; } = [];
        public List<string> Allow { get; } = [];

        /// <summary>
        /// A raiz está bloqueada para este grupo?
        ///
        /// <c>Allow: /</c> explícito vence, porque é a forma usual de abrir uma
        /// exceção dentro de um site que bloqueia tudo por padrão.
        /// </summary>
        public bool BlocksRoot =>
            !Allow.Contains("/") && Disallow.Any(d => d == "/");
    }

    private static List<Group> Parse(string content)
    {
        var groups = new List<Group>();
        Group? current = null;
        var lastLineWasAgent = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw;

            // Comentário pode vir no fim da linha, não só sozinho.
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];

            line = line.Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var field = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (string.Equals(field, "user-agent", StringComparison.OrdinalIgnoreCase))
            {
                // Regra 1: agentes consecutivos compartilham o mesmo grupo.
                if (current is null || !lastLineWasAgent)
                {
                    current = new Group();
                    groups.Add(current);
                }

                current.Agents.Add(value);
                lastLineWasAgent = true;
                continue;
            }

            lastLineWasAgent = false;

            if (current is null) continue;

            if (string.Equals(field, "disallow", StringComparison.OrdinalIgnoreCase))
            {
                // `Disallow:` vazio significa "permite tudo" — não é bloqueio.
                if (value.Length > 0) current.Disallow.Add(value);
            }
            else if (string.Equals(field, "allow", StringComparison.OrdinalIgnoreCase))
            {
                current.Allow.Add(value);
            }
        }

        return groups;
    }
}
