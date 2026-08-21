namespace AutoHous.Revenue.Domain;

/// <summary>Espelha o enum Postgres <c>account_status</c> (migration 0001).</summary>
public enum AccountStatus
{
    Discovered,
    Researching,
    Researched,
    Scored,
    Ready,
    Contacted,
    Engaged,
    Nurture,
    Suppressed,
    Customer,
    Rejected
}

/// <summary>Espelha o enum Postgres <c>contact_status</c>.</summary>
public enum ContactStatus
{
    Discovered,
    Verified,
    Invalid,
    Suppressed
}

/// <summary>
/// Espelha <c>opportunity_stage</c>. Os estagios de negociacao vivem na
/// oportunidade, nao na account: o diagrama da secao 18 do blueprint funde dois
/// ciclos de vida, e uma account pode ter varias oportunidades simultaneas.
/// </summary>
public enum OpportunityStage
{
    Meeting,
    Sql,
    Discovery,
    Proposal,
    Negotiation,
    Won,
    Lost
}

/// <summary>Espelha <c>evidence_type</c>.</summary>
public enum EvidenceType
{
    CompanyRegistry,
    Website,
    Search,
    Social,
    News,
    JobPosting,
    Marketplace,
    Manual,
    Other
}

public static class EnumExtensions
{
    /// <summary>Converte para o literal snake_case usado pelos enums do Postgres.</summary>
    public static string ToDbValue<TEnum>(this TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString()!;
        var sb = new System.Text.StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }

    public static TEnum FromDbValue<TEnum>(string value) where TEnum : struct, Enum
    {
        var pascal = string.Concat(
            value.Split('_', StringSplitOptions.RemoveEmptyEntries)
                 .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

        return Enum.Parse<TEnum>(pascal, ignoreCase: true);
    }
}
