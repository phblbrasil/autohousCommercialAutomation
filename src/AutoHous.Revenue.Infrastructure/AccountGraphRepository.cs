using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Account graph: busca de candidatos por raiz de CNPJ e trigrama, criacao e
/// vinculo de contas, e a fila de revisao (migration 0012).
/// </summary>
public sealed class AccountGraphRepository(
    NpgsqlConnectionFactory connections) : IAccountGraphRepository
{
    private const string CandidateColumns = """
        a.id              as AccountId,
        a.name            as Name,
        a.normalized_name as NormalizedName,
        a.state           as Uf,
        a.city            as City
        """;

    /// <summary>
    /// Traz duas familias de candidato em uma consulta: contas que ja tem a
    /// mesma raiz de CNPJ, e contas com nome parecido.
    ///
    /// As duas precisam vir juntas porque a decisao do
    /// <see cref="AccountGroupResolver"/> depende de ver as duas: raiz igual
    /// vence nome parecido, e a funcao nao pode escolher entre o que nao recebeu.
    /// A busca por raiz nao respeita o limite de similaridade — ela e identidade,
    /// nao semelhanca.
    /// </summary>
    public async Task<IReadOnlyList<AccountGroupCandidate>> FindCandidatesAsync(
        string cnpjRoot, string normalizedName, decimal minimumSimilarity, int limit,
        CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<CandidateRow>(new CommandDefinition($"""
            with por_raiz as (
                select distinct {CandidateColumns},
                       1.0::numeric as NameSimilarity
                  from accounts a
                  join companies_cnpj c on c.account_id = a.id
                 where left(c.cnpj, 8) = @CnpjRoot
            ),
            por_nome as (
                select {CandidateColumns},
                       round(similarity(a.normalized_name, @NormalizedName)::numeric, 4) as NameSimilarity
                  from accounts a
                 where a.normalized_name is not null
                   and a.normalized_name % @NormalizedName
                   and similarity(a.normalized_name, @NormalizedName) >= @MinSimilarity
                 order by NameSimilarity desc
                 limit @Limit
            ),
            unido as (
                select * from por_raiz
                union
                select * from por_nome
            )
            select u.AccountId, u.Name, u.NormalizedName, u.Uf, u.City, u.NameSimilarity,
                   coalesce(
                       (select array_agg(distinct left(c.cnpj, 8))
                          from companies_cnpj c
                         where c.account_id = u.AccountId),
                       array[]::text[]) as CnpjRoots
              from unido u
             order by u.NameSimilarity desc
            """,
            new
            {
                CnpjRoot = cnpjRoot,
                NormalizedName = normalizedName,
                MinSimilarity = minimumSimilarity,
                Limit = limit
            }, cancellationToken: ct));

        return [.. rows.Select(r => new AccountGroupCandidate
        {
            AccountId = r.AccountId,
            Name = r.Name,
            NormalizedName = r.NormalizedName,
            Uf = r.Uf,
            City = r.City,
            NameSimilarity = r.NameSimilarity,
            CnpjRoots = r.CnpjRoots ?? []
        })];
    }

    public async Task<Guid> CreateAccountForCompanyAsync(
        IUnitOfWork uow, Guid accountId, NormalizedCompany company, decimal graphConfidence,
        CancellationToken ct = default)
    {
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into accounts
                (id, name, normalized_name, segment, state, city, status, graph_confidence)
            values
                (@Id, @Name, @NormalizedName, @Segment, @State, @City,
                 'discovered'::account_status, @GraphConfidence)
            """,
            new
            {
                Id = accountId,
                Name = company.DisplayName,
                company.NormalizedName,
                Segment = CnaeCatalog.ToSegment(company.Cnae.Operation),
                State = company.Uf,
                City = company.Municipio,
                GraphConfidence = graphConfidence
            }, uow.Tx(), cancellationToken: ct));

        await AttachCompanyAsync(uow, accountId, company, ct);
        return accountId;
    }

    /// <summary>
    /// Vincula o CNPJ a conta. <c>on conflict (cnpj)</c> atualiza o cadastro e o
    /// dono: uma recarga da base traz situacao cadastral e nome fantasia
    /// atualizados, e sobrescrever e o comportamento correto — a Receita e a
    /// autoridade sobre estes campos.
    ///
    /// <c>coalesce(excluded.x, x)</c> em cada coluna e deliberado: uma carga que
    /// nao traz endereco (uma lista de CNPJs colada de planilha) nao pode apagar
    /// o endereco que a carga da Receita gravou no mes passado.
    /// </summary>
    public async Task AttachCompanyAsync(
        IUnitOfWork uow, Guid accountId, NormalizedCompany company, CancellationToken ct = default)
    {
        var companyId = await uow.Db().ExecuteScalarAsync<Guid>(new CommandDefinition("""
            insert into companies_cnpj
                (id, account_id, cnpj, razao_social, nome_fantasia, cnae_principal,
                 cnaes_secundarios, situacao_cadastral, data_situacao_cadastral,
                 motivo_situacao_cadastral, natureza_juridica, porte, capital_social,
                 data_abertura, matriz_filial, municipio, municipio_codigo, uf,
                 cep, logradouro, numero, complemento, bairro,
                 telefone_1, telefone_2, email, opcao_simples, opcao_mei, source_updated_at)
            values
                (@Id, @AccountId, @Cnpj, @RazaoSocial, @NomeFantasia, @Cnae,
                 @CnaesSecundarios::jsonb, @Situacao, @DataSituacao,
                 @MotivoSituacao, @NaturezaJuridica, @Porte, @CapitalSocial,
                 @DataAbertura, @MatrizFilial, @Municipio, @MunicipioCodigo, @Uf,
                 @Cep, @Logradouro, @Numero, @Complemento, @Bairro,
                 @Telefone1, @Telefone2, @Email, @OpcaoSimples, @OpcaoMei, now())
            on conflict (cnpj) do update
                set account_id        = excluded.account_id,
                    razao_social      = coalesce(excluded.razao_social, companies_cnpj.razao_social),
                    nome_fantasia     = coalesce(excluded.nome_fantasia, companies_cnpj.nome_fantasia),
                    cnae_principal    = coalesce(excluded.cnae_principal, companies_cnpj.cnae_principal),
                    cnaes_secundarios = coalesce(excluded.cnaes_secundarios, companies_cnpj.cnaes_secundarios),
                    situacao_cadastral= coalesce(excluded.situacao_cadastral, companies_cnpj.situacao_cadastral),
                    data_situacao_cadastral = coalesce(excluded.data_situacao_cadastral, companies_cnpj.data_situacao_cadastral),
                    motivo_situacao_cadastral = coalesce(excluded.motivo_situacao_cadastral, companies_cnpj.motivo_situacao_cadastral),
                    natureza_juridica = coalesce(excluded.natureza_juridica, companies_cnpj.natureza_juridica),
                    porte             = coalesce(excluded.porte, companies_cnpj.porte),
                    capital_social    = coalesce(excluded.capital_social, companies_cnpj.capital_social),
                    data_abertura     = coalesce(excluded.data_abertura, companies_cnpj.data_abertura),
                    matriz_filial     = coalesce(excluded.matriz_filial, companies_cnpj.matriz_filial),
                    municipio         = coalesce(excluded.municipio, companies_cnpj.municipio),
                    municipio_codigo  = coalesce(excluded.municipio_codigo, companies_cnpj.municipio_codigo),
                    uf                = coalesce(excluded.uf, companies_cnpj.uf),
                    cep               = coalesce(excluded.cep, companies_cnpj.cep),
                    logradouro        = coalesce(excluded.logradouro, companies_cnpj.logradouro),
                    numero            = coalesce(excluded.numero, companies_cnpj.numero),
                    complemento       = coalesce(excluded.complemento, companies_cnpj.complemento),
                    bairro            = coalesce(excluded.bairro, companies_cnpj.bairro),
                    telefone_1        = coalesce(excluded.telefone_1, companies_cnpj.telefone_1),
                    telefone_2        = coalesce(excluded.telefone_2, companies_cnpj.telefone_2),
                    email             = coalesce(excluded.email, companies_cnpj.email),
                    opcao_simples     = coalesce(excluded.opcao_simples, companies_cnpj.opcao_simples),
                    opcao_mei         = coalesce(excluded.opcao_mei, companies_cnpj.opcao_mei),
                    source_updated_at = now()
            returning id
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                company.Cnpj,
                company.RazaoSocial,
                company.NomeFantasia,
                Cnae = company.Cnae.Code,
                CnaesSecundarios = company.CnaesSecundarios.Count == 0
                    ? null
                    : JsonSerializer.Serialize(company.CnaesSecundarios),
                Situacao = company.SituacaoCadastral,
                DataSituacao = company.DataSituacaoCadastral?.ToDateTime(TimeOnly.MinValue),
                MotivoSituacao = company.MotivoSituacaoCadastral,
                company.NaturezaJuridica,
                company.Porte,
                company.CapitalSocial,
                DataAbertura = company.DataAbertura?.ToDateTime(TimeOnly.MinValue),
                company.MatrizFilial,
                company.Municipio,
                company.MunicipioCodigo,
                Uf = company.Uf,
                company.Cep,
                company.Logradouro,
                company.Numero,
                company.Complemento,
                company.Bairro,
                company.Telefone1,
                company.Telefone2,
                company.Email,
                company.OpcaoSimples,
                company.OpcaoMei
            }, uow.Tx(), cancellationToken: ct));

        await UpsertLocationAsync(uow, accountId, companyId, company, ct);
    }

    /// <summary>
    /// Cada estabelecimento da Receita e uma LOJA da conta. Gravar isso e o que
    /// faz <c>accounts.store_count</c> deixar de ser um campo vazio: um grupo com
    /// 8 CNPJs ativos e um grupo de 8 lojas, e essa e a diferenca entre motion de
    /// enterprise e motion de revenda.
    ///
    /// Idempotente pelo indice <c>account_locations_identity_uq</c> da 0010, que
    /// e por expressao - dai o predicado repetido no <c>on conflict</c>. O nome
    /// carrega o logradouro porque a chave e (conta, nome, cidade): duas lojas do
    /// mesmo grupo na mesma cidade colapsariam em uma se o nome fosse so o da
    /// bandeira.
    /// </summary>
    private static async Task UpsertLocationAsync(
        IUnitOfWork uow, Guid accountId, Guid companyId, NormalizedCompany company, CancellationToken ct)
    {
        if (company.Logradouro is null && company.Municipio is null) return;

        var street = string.Join(", ",
            new[] { company.Logradouro, company.Numero }.Where(p => !string.IsNullOrWhiteSpace(p)));

        var address = string.Join(" - ",
            new[] { street, company.Bairro, company.Cep }.Where(p => !string.IsNullOrWhiteSpace(p)));

        var name = street.Length > 0
            ? $"{company.DisplayName} - {street}"
            : company.DisplayName;

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into account_locations
                (id, account_id, company_cnpj_id, location_type, name, address, city, state, is_active, confidence)
            values
                (@Id, @AccountId, @CompanyId, @LocationType, @Name, @Address, @City, @State, @IsActive, 1.0)
            on conflict (account_id, coalesce(name, ''), coalesce(city, '')) do update
                set company_cnpj_id = excluded.company_cnpj_id,
                    location_type   = excluded.location_type,
                    address         = coalesce(excluded.address, account_locations.address),
                    state           = coalesce(excluded.state, account_locations.state),
                    is_active       = excluded.is_active
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                CompanyId = companyId,
                LocationType = company.MatrizFilial switch
                {
                    "1" => "matriz",
                    "2" => "filial",
                    _ => company.IsHeadquarters ? "matriz" : "filial"
                },
                Name = name,
                Address = address.Length > 0 ? address : null,
                City = company.Municipio,
                State = company.Uf,
                IsActive = CompanyNormalizer.IsActiveRegistration(company.SituacaoCadastral)
            }, uow.Tx(), cancellationToken: ct));
    }

    public async Task<Guid?> FindAccountByCnpjAsync(string cnpj, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select account_id from companies_cnpj where cnpj = @Cnpj and account_id is not null",
            new { Cnpj = cnpj }, cancellationToken: ct));
    }

    public async Task RecordMergeCandidateAsync(
        IUnitOfWork uow, Guid candidateId, MergeCandidateRecord record, CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into account_merge_candidates
                (id, account_id, raw_id, incoming_cnpj, incoming_name,
                 incoming_uf, incoming_municipio, similarity, reason)
            values
                (@Id, @AccountId, @RawId, @IncomingCnpj, @IncomingName,
                 @IncomingUf, @IncomingMunicipio, @Similarity, @Reason)
            -- account_merge_candidates_pending_uq e um indice PARCIAL; a
            -- inferencia de ON CONFLICT sobre indice parcial exige repetir o
            -- predicado no comando.
            on conflict (account_id, incoming_cnpj) where status = 'pending' do nothing
            """,
            new
            {
                Id = candidateId,
                record.AccountId,
                record.RawId,
                record.IncomingCnpj,
                record.IncomingName,
                record.IncomingUf,
                record.IncomingMunicipio,
                record.Similarity,
                record.Reason
            }, uow.Tx(), cancellationToken: ct));

    public async Task<IReadOnlyList<MergeCandidateView>> ListPendingCandidatesAsync(
        int limit, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<MergeCandidateView>(new CommandDefinition($"""
            {CandidateViewSelect}
             where m.status = 'pending'
             order by m.similarity desc
             limit @Limit
            """, new { Limit = limit }, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<MergeCandidateView?> GetCandidateAsync(Guid candidateId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<MergeCandidateView>(new CommandDefinition(
            $"{CandidateViewSelect} where m.id = @Id",
            new { Id = candidateId }, cancellationToken: ct));
    }

    public async Task DecideCandidateAsync(
        IUnitOfWork uow, Guid candidateId, bool approved, string? decidedBy,
        CancellationToken ct = default) =>
        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update account_merge_candidates
               set status = @Status, decided_by = @DecidedBy, decided_at = now()
             where id = @Id and status = 'pending'
            """,
            new
            {
                Id = candidateId,
                Status = approved ? "approved" : "rejected",
                DecidedBy = decidedBy
            }, uow.Tx(), cancellationToken: ct));

    private const string CandidateViewSelect = """
        select m.id                 as Id,
               m.account_id         as AccountId,
               a.name               as AccountName,
               a.state              as AccountUf,
               m.raw_id             as RawId,
               m.incoming_cnpj      as IncomingCnpj,
               m.incoming_name      as IncomingName,
               m.incoming_uf        as IncomingUf,
               m.incoming_municipio as IncomingMunicipio,
               m.similarity         as Similarity,
               m.reason             as Reason,
               m.status             as Status
          from account_merge_candidates m
          join accounts a on a.id = m.account_id
        """;

    private sealed record CandidateRow
    {
        public Guid AccountId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? NormalizedName { get; init; }
        public string? Uf { get; init; }
        public string? City { get; init; }
        public decimal NameSimilarity { get; init; }
        public string[]? CnpjRoots { get; init; }
    }
}
