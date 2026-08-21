namespace AutoHous.Revenue.Application;

/// <summary>
/// Um socio, como a Receita publica. O CPF ja chega mascarado da origem
/// (art. 129 §2o da Lei 13.473/2017) e e guardado exatamente assim.
/// </summary>
public sealed record CompanyPartnerRecord
{
    public required string CnpjBasico { get; init; }
    public string? Identificador { get; init; }
    public string? Nome { get; init; }
    public string? CpfCnpjMascarado { get; init; }
    public string? Qualificacao { get; init; }
    public DateOnly? DataEntrada { get; init; }
    public string? Pais { get; init; }
    public string? RepresentanteCpf { get; init; }
    public string? RepresentanteNome { get; init; }
    public string? RepresentanteQualificacao { get; init; }
    public string? FaixaEtaria { get; init; }
}

/// <summary>
/// Quadro societario (<c>company_partners</c>, migration 0014).
///
/// Porta separada das demais de propósito: e a unica que escreve PII de pessoa
/// fisica, e a carga so a usa quando o operador passa <c>--socios</c>. Manter o
/// contrato isolado deixa visivel, no grafo de dependencias, quem toca esse dado.
/// </summary>
public interface ICompanyPartnerRepository
{
    /// <summary>Grava um lote de socios. Reexecutar a carga atualiza, nao duplica.</summary>
    Task<int> UpsertAsync(
        IUnitOfWork uow, string release, IReadOnlyList<CompanyPartnerRecord> partners, CancellationToken ct = default);

    Task<IReadOnlyList<CompanyPartnerRecord>> ListByCnpjBasicoAsync(
        string cnpjBasico, CancellationToken ct = default);
}
