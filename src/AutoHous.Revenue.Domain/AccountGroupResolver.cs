namespace AutoHous.Revenue.Domain;

/// <summary>
/// Faixas de decisao do account graph.
///
/// Vivem no dominio - e nao embutidas no SQL - porque sao regra de negocio: e o
/// ponto em que a plataforma decide sozinha unir dois CNPJs, e quando ela para e
/// pergunta. O adaptador de busca por trigrama recebe estes valores como
/// parametro, para que exista uma unica definicao.
/// </summary>
public static class AccountSimilarity
{
    /// <summary>Acima disto, une sem perguntar.</summary>
    public const decimal Auto = 0.90m;

    /// <summary>Entre esta faixa e <see cref="Auto"/>, vai para revisao humana.</summary>
    public const decimal Probable = 0.75m;

    public static string Classify(decimal similarity) => similarity switch
    {
        >= Auto => "auto",
        >= Probable => "provavel",
        _ => "revisao"
    };
}

/// <summary>Conta ja existente que pode ser o mesmo grupo economico da empresa que chega.</summary>
public sealed record AccountGroupCandidate
{
    public required Guid AccountId { get; init; }
    public required string Name { get; init; }
    public string? NormalizedName { get; init; }
    public string? Uf { get; init; }
    public string? City { get; init; }

    /// <summary>Alguma das raizes de CNPJ ja ligadas a esta conta.</summary>
    public IReadOnlyList<string> CnpjRoots { get; init; } = [];

    /// <summary>Similaridade de trigrama entre nomes normalizados, calculada pelo Postgres.</summary>
    public decimal NameSimilarity { get; init; }
}

public enum AccountGroupAction
{
    /// <summary>Nada plausivel na base: nasce uma conta.</summary>
    CreateAccount,

    /// <summary>Identidade suficiente para unir sem intervencao.</summary>
    AttachToExisting,

    /// <summary>Parecido demais para ignorar, diferente demais para unir sozinho.</summary>
    SendToReview
}

public sealed record AccountGroupDecision(
    AccountGroupAction Action,
    Guid? AccountId,
    decimal Confidence,
    string Reason);

/// <summary>
/// Etapa 03 do pipeline: decide se um CNPJ que chega abre uma conta nova, entra
/// numa existente, ou vira item de fila de revisao.
///
/// E o principio de desenho numero 1 da V2 tornado codigo: <em>Account > CNPJ</em>.
/// Prospectar por CNPJ isolado faz dois SDRs atacarem a mesma matriz por filiais
/// diferentes.
///
/// Deliberadamente puro: recebe os candidatos ja buscados e devolve a decisao.
/// Quem faz a busca por trigrama e o adaptador de persistencia; quem decide e
/// esta funcao, testavel com uma lista em memoria.
/// </summary>
public static class AccountGroupResolver
{
    public static AccountGroupDecision Resolve(
        NormalizedCompany company, IReadOnlyList<AccountGroupCandidate> candidates)
    {
        // 1. Raiz de CNPJ. Filial e matriz compartilham os oito primeiros digitos
        //    por definicao da Receita: nao ha julgamento a fazer.
        var sameRoot = candidates.FirstOrDefault(
            c => c.CnpjRoots.Contains(company.CnpjRoot, StringComparer.Ordinal));

        if (sameRoot is not null)
        {
            return new AccountGroupDecision(
                AccountGroupAction.AttachToExisting, sameRoot.AccountId, 1.00m, "cnpj_root");
        }

        var best = candidates
            .OrderByDescending(c => c.NameSimilarity)
            .FirstOrDefault();

        if (best is null || best.NameSimilarity < AccountSimilarity.Probable)
        {
            // Abaixo da faixa de revisao nao existe candidato plausivel; a conta e
            // nova e a confianca nisso e total. Confundir "sem candidato" com
            // "baixa confianca" encheria a fila de revisao com conta legitima.
            return new AccountGroupDecision(
                AccountGroupAction.CreateAccount, null, 1.00m, "no_candidate");
        }

        // 2. Nome muito parecido e mesma UF. Grupos economicos automotivos sao
        //    regionais: "Vento Sul Veiculos" em SP e "Vento Sul Veiculos" em RS
        //    sao, quase sempre, empresas diferentes com o mesmo nome generico.
        var sameState =
            !string.IsNullOrEmpty(company.Uf) &&
            string.Equals(best.Uf, company.Uf, StringComparison.OrdinalIgnoreCase);

        if (best.NameSimilarity >= AccountSimilarity.Auto && sameState)
        {
            return new AccountGroupDecision(
                AccountGroupAction.AttachToExisting, best.AccountId, best.NameSimilarity, "name_and_uf");
        }

        // 3. Resto da faixa: humano decide. Um falso merge custa mais que um
        //    falso split - desfazer exige reconstruir evidencia e historico.
        return new AccountGroupDecision(
            AccountGroupAction.SendToReview,
            best.AccountId,
            best.NameSimilarity,
            best.NameSimilarity >= AccountSimilarity.Auto ? "name_match_other_uf" : "name_similarity");
    }
}
