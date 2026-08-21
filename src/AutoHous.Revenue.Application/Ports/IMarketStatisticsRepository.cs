using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Persistencia do agregado de mercado (<c>rf_cnae_stats</c>, <c>rf_municipio_stats</c>).
///
/// A gravacao e por release e substitui o que existia: recarregar a mesma
/// competencia corrige a contagem em vez de somar em cima dela.
/// </summary>
public interface IMarketStatisticsRepository
{
    Task ReplaceAsync(
        IUnitOfWork uow,
        string release,
        IReadOnlyList<CnaeStatRow> byCnae,
        IReadOnlyList<MunicipioStatRow> byMunicipio,
        IReadOnlyDictionary<string, string> municipioNames,
        CancellationToken ct = default);

    /// <summary>Total de estabelecimentos contados no release, para conferencia.</summary>
    Task<long> CountEstablishmentsAsync(string release, CancellationToken ct = default);
}
