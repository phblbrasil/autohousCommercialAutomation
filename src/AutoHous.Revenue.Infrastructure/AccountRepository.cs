using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

public sealed class AccountRepository(
    NpgsqlConnectionFactory connections,
    IUnitOfWorkFactory unitOfWork) : IAccountRepository
{
    private const string SelectColumns = """
        id as Id, name as Name, normalized_name as NormalizedName, domain as Domain,
        segment as Segment, tier as Tier, state as State, city as City,
        status::text as StatusText, store_count as StoreCount,
        vehicle_inventory_estimate as VehicleInventoryEstimate,
        graph_confidence as GraphConfidence, research_completeness as ResearchCompleteness,
        last_researched_at as LastResearchedAt, next_research_at as NextResearchAt,
        created_at as CreatedAt, updated_at as UpdatedAt
        """;

    public async Task<Account?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            $"select {SelectColumns} from accounts where id = @Id", new { Id = id }, cancellationToken: ct));

        return row?.ToAccount();
    }

    public async Task<Account?> GetByCnpjAsync(string cnpj, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            $"""
             select {SelectColumns}
               from accounts a
               join companies_cnpj c on c.account_id = a.id
              -- cast explicito: `cnpj` e character(14) e o parametro chega como
              -- text, o que faz o Postgres comparar `(cnpj)::text` e descartar o
              -- indice. Ver AccountGraphRepository.FindAccountByCnpjAsync.
              where c.cnpj = cast(@Cnpj as char(14))
             """, new { Cnpj = cnpj }, cancellationToken: ct));

        return row?.ToAccount();
    }

    /// <summary>
    /// Cria account + companies_cnpj em uma transacao. Reexecutar com o mesmo CNPJ
    /// devolve a account existente em vez de duplicar (item 1-2 do DoD).
    /// </summary>
    public async Task<Guid> CreateFromCnpjAsync(
        string cnpj, string name, string? razaoSocial, string? uf, string? municipio,
        CancellationToken ct = default)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);

        var existing = await uow.Db().ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select account_id from companies_cnpj where cnpj = cast(@Cnpj as char(14))",
            new { Cnpj = cnpj }, uow.Tx(), cancellationToken: ct));

        if (existing is { } found && found != Guid.Empty)
        {
            return found;
        }

        var accountId = Guid.CreateVersion7();

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into accounts (id, name, normalized_name, state, city, status)
            values (@Id, @Name, @NormalizedName, @State, @City, 'discovered'::account_status)
            """,
            new
            {
                Id = accountId,
                Name = name,
                NormalizedName = NameNormalizer.Normalize(name),
                State = uf,
                City = municipio
            }, uow.Tx(), cancellationToken: ct));

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            insert into companies_cnpj (id, account_id, cnpj, razao_social, uf, municipio)
            values (@Id, @AccountId, @Cnpj, @RazaoSocial, @Uf, @Municipio)
            on conflict (cnpj) do update set account_id = excluded.account_id
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                Cnpj = cnpj,
                RazaoSocial = razaoSocial,
                Uf = uf,
                Municipio = municipio
            }, uow.Tx(), cancellationToken: ct));

        await uow.CommitAsync(ct);
        return accountId;
    }

    /// <summary>
    /// Muda o status validando a transicao pela maquina de estados do dominio.
    /// Nenhum handler deve escrever accounts.status por outro caminho.
    /// </summary>
    public async Task TransitionAsync(
        IUnitOfWork uow, Guid accountId, AccountStatus from, AccountStatus to, CancellationToken ct = default)
    {
        AccountStatusTransitions.EnsureCanTransition(from, to);

        await uow.Db().ExecuteAsync(new CommandDefinition("""
            update accounts set status = @Status::account_status where id = @Id
            """,
            new { Id = accountId, Status = to.ToDbValue() },
            uow.Tx(), cancellationToken: ct));
    }

    public async Task<AccountContext?> GetContextAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.QuerySingleOrDefaultAsync<AccountContext>(new CommandDefinition("""
            select a.id                     as AccountId,
                   a.name                   as Name,
                   a.domain                 as Domain,
                   a.segment                as Segment,
                   a.city                   as City,
                   a.state                  as State,
                   a.status::text           as Status,
                   a.store_count            as StoreCount,
                   a.last_researched_at     as LastResearchedAt,
                   coalesce((select array_agg(c.cnpj) from companies_cnpj c where c.account_id = a.id), '{}') as Cnpjs,
                   coalesce((select array_agg(b.brand) from account_brands b where b.account_id = a.id), '{}') as KnownBrands,
                   (select count(*) from evidence e where e.account_id = a.id) as EvidenceCount
              from accounts a
             where a.id = @Id
            """, new { Id = id }, cancellationToken: ct));
    }

    private sealed record AccountRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? NormalizedName { get; init; }
        public string? Domain { get; init; }
        public string? Segment { get; init; }
        public short? Tier { get; init; }
        public string? State { get; init; }
        public string? City { get; init; }
        public string StatusText { get; init; } = "discovered";
        public int? StoreCount { get; init; }
        public int? VehicleInventoryEstimate { get; init; }
        public decimal? GraphConfidence { get; init; }
        public decimal? ResearchCompleteness { get; init; }
        public DateTimeOffset? LastResearchedAt { get; init; }
        public DateTimeOffset? NextResearchAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Account ToAccount() => new()
        {
            Id = Id,
            Name = Name,
            NormalizedName = NormalizedName,
            Domain = Domain,
            Segment = Segment,
            Tier = Tier,
            State = State,
            City = City,
            Status = EnumExtensions.FromDbValue<AccountStatus>(StatusText),
            StoreCount = StoreCount,
            VehicleInventoryEstimate = VehicleInventoryEstimate,
            GraphConfidence = GraphConfidence,
            ResearchCompleteness = ResearchCompleteness,
            LastResearchedAt = LastResearchedAt,
            NextResearchAt = NextResearchAt,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}
