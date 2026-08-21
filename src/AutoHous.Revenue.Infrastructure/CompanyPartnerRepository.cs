using AutoHous.Revenue.Application;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Quadro societario (migration 0014).
///
/// Unico adaptador do sistema que escreve PII de pessoa fisica. A carga so o
/// aciona com <c>--socios</c>, e a tabela vive em migration propria justamente
/// para que "parar de guardar isso" seja um <c>drop table</c> e nao um projeto.
/// </summary>
public sealed class CompanyPartnerRepository(
    NpgsqlConnectionFactory connections) : ICompanyPartnerRepository
{
    public async Task<int> UpsertAsync(
        IUnitOfWork uow, string release, IReadOnlyList<CompanyPartnerRecord> partners,
        CancellationToken ct = default)
    {
        if (partners.Count == 0) return 0;

        return await uow.Db().ExecuteScalarAsync<int>(new CommandDefinition("""
            with entrada as (
                select unnest(@Ids::uuid[])       as id,
                       unnest(@Cnpjs::char(8)[])  as cnpj_basico,
                       unnest(@Identificadores::text[]) as identificador,
                       unnest(@Nomes::text[])     as nome,
                       unnest(@Documentos::text[]) as cpf_cnpj_mascarado,
                       unnest(@Qualificacoes::text[]) as qualificacao,
                       unnest(@Entradas::date[])  as data_entrada,
                       unnest(@Paises::text[])    as pais,
                       unnest(@RepCpfs::text[])   as representante_cpf,
                       unnest(@RepNomes::text[])  as representante_nome,
                       unnest(@RepQuals::text[])  as representante_qualificacao,
                       unnest(@Faixas::text[])    as faixa_etaria
            ),
            gravado as (
                insert into company_partners
                    (id, cnpj_basico, identificador, nome, cpf_cnpj_mascarado, qualificacao,
                     data_entrada, pais, representante_cpf, representante_nome,
                     representante_qualificacao, faixa_etaria, release)
                select id, cnpj_basico, identificador, nome, cpf_cnpj_mascarado, qualificacao,
                       data_entrada, pais, representante_cpf, representante_nome,
                       representante_qualificacao, faixa_etaria, @Release
                  from entrada
                -- company_partners_identity_uq e um indice por EXPRESSAO; a
                -- inferencia de ON CONFLICT exige repetir as expressoes.
                on conflict (cnpj_basico, coalesce(cpf_cnpj_mascarado, ''), coalesce(nome, ''))
                do update set qualificacao = excluded.qualificacao,
                              data_entrada = excluded.data_entrada,
                              representante_cpf = excluded.representante_cpf,
                              representante_nome = excluded.representante_nome,
                              representante_qualificacao = excluded.representante_qualificacao,
                              faixa_etaria = excluded.faixa_etaria,
                              release = excluded.release
                returning 1
            )
            select count(*)::int from gravado
            """,
            new
            {
                Release = release,
                Ids = partners.Select(_ => Guid.CreateVersion7()).ToArray(),
                Cnpjs = partners.Select(p => p.CnpjBasico).ToArray(),
                Identificadores = partners.Select(p => p.Identificador).ToArray(),
                Nomes = partners.Select(p => p.Nome).ToArray(),
                Documentos = partners.Select(p => p.CpfCnpjMascarado).ToArray(),
                Qualificacoes = partners.Select(p => p.Qualificacao).ToArray(),
                Entradas = partners.Select(p => p.DataEntrada?.ToDateTime(TimeOnly.MinValue)).ToArray(),
                Paises = partners.Select(p => p.Pais).ToArray(),
                RepCpfs = partners.Select(p => p.RepresentanteCpf).ToArray(),
                RepNomes = partners.Select(p => p.RepresentanteNome).ToArray(),
                RepQuals = partners.Select(p => p.RepresentanteQualificacao).ToArray(),
                Faixas = partners.Select(p => p.FaixaEtaria).ToArray()
            }, uow.Tx(), cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CompanyPartnerRecord>> ListByCnpjBasicoAsync(
        string cnpjBasico, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<CompanyPartnerRecord>(new CommandDefinition("""
            select cnpj_basico                as CnpjBasico,
                   identificador              as Identificador,
                   nome                       as Nome,
                   cpf_cnpj_mascarado         as CpfCnpjMascarado,
                   qualificacao               as Qualificacao,
                   data_entrada               as DataEntrada,
                   pais                       as Pais,
                   representante_cpf          as RepresentanteCpf,
                   representante_nome         as RepresentanteNome,
                   representante_qualificacao as RepresentanteQualificacao,
                   faixa_etaria               as FaixaEtaria
              from company_partners
             where cnpj_basico = @Cnpj
             order by nome
            """,
            new { Cnpj = cnpjBasico }, cancellationToken: ct));

        return rows.ToList();
    }
}
