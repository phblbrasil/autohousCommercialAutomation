using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Agregado de mercado (migration 0013).
///
/// A gravacao apaga o release e reescreve. Fazer upsert linha a linha deixaria
/// para tras as celulas que existiam na carga anterior e sumiram desta - um CNAE
/// que zerou numa UF continuaria mostrando o numero do mes passado, o que e pior
/// que nao ter numero.
/// </summary>
public sealed class MarketStatisticsRepository(
    NpgsqlConnectionFactory connections) : IMarketStatisticsRepository
{
    /// <summary>
    /// Blocos de 10 mil. O agregado nacional chega a centenas de milhares de
    /// celulas, e mandar tudo num unico comando estoura o limite de parametros
    /// do protocolo.
    /// </summary>
    private const int ChunkSize = 10_000;

    public async Task ReplaceAsync(
        IUnitOfWork uow,
        string release,
        IReadOnlyList<CnaeStatRow> byCnae,
        IReadOnlyList<MunicipioStatRow> byMunicipio,
        IReadOnlyDictionary<string, string> municipioNames,
        CancellationToken ct = default)
    {
        await uow.Db().ExecuteAsync(new CommandDefinition(
            """
            delete from rf_cnae_stats where release = @Release;
            delete from rf_municipio_stats where release = @Release;
            """,
            new { Release = release }, uow.Tx(), cancellationToken: ct));

        foreach (var chunk in byCnae.Chunk(ChunkSize))
        {
            await uow.Db().ExecuteAsync(new CommandDefinition("""
                insert into rf_cnae_stats
                    (release, uf, cnae, situacao_cadastral, matriz_filial, establishments)
                select @Release,
                       unnest(@Ufs::text[]),
                       unnest(@Cnaes::text[]),
                       unnest(@Situacoes::text[]),
                       unnest(@MatrizFilial::text[]),
                       unnest(@Counts::bigint[])
                """,
                new
                {
                    Release = release,
                    Ufs = chunk.Select(r => r.Uf).ToArray(),
                    Cnaes = chunk.Select(r => r.Cnae).ToArray(),
                    Situacoes = chunk.Select(r => r.SituacaoCadastral).ToArray(),
                    MatrizFilial = chunk.Select(r => r.MatrizFilial).ToArray(),
                    Counts = chunk.Select(r => r.Establishments).ToArray()
                }, uow.Tx(), cancellationToken: ct));
        }

        foreach (var chunk in byMunicipio.Chunk(ChunkSize))
        {
            await uow.Db().ExecuteAsync(new CommandDefinition("""
                insert into rf_municipio_stats
                    (release, uf, municipio_codigo, municipio_nome, cnae, situacao_cadastral, establishments)
                select @Release,
                       unnest(@Ufs::text[]),
                       unnest(@Codigos::text[]),
                       unnest(@Nomes::text[]),
                       unnest(@Cnaes::text[]),
                       unnest(@Situacoes::text[]),
                       unnest(@Counts::bigint[])
                """,
                new
                {
                    Release = release,
                    Ufs = chunk.Select(r => r.Uf).ToArray(),
                    Codigos = chunk.Select(r => r.MunicipioCodigo).ToArray(),
                    // Desnormalizado de proposito: o codigo da RF sozinho nao e
                    // legivel em relatorio nenhum, e a tabela de dominio nao vive
                    // no banco.
                    Nomes = chunk
                        .Select(r => municipioNames.TryGetValue(r.MunicipioCodigo, out var name) ? name : null)
                        .ToArray(),
                    Cnaes = chunk.Select(r => r.Cnae).ToArray(),
                    Situacoes = chunk.Select(r => r.SituacaoCadastral).ToArray(),
                    Counts = chunk.Select(r => r.Establishments).ToArray()
                }, uow.Tx(), cancellationToken: ct));
        }
    }

    public async Task<long> CountEstablishmentsAsync(string release, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "select coalesce(sum(establishments), 0)::bigint from rf_cnae_stats where release = @Release",
            new { Release = release }, cancellationToken: ct));
    }
}
