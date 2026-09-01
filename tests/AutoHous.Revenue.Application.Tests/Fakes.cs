using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application.Tests;

/// <summary>
/// Portas falsas em memoria.
///
/// A existencia desta suite e a prova de que o refactor funcionou: antes, testar
/// "conta suprimida nao entra em pesquisa" exigia subir um Postgres em container
/// e um WebApplicationFactory. Agora a mesma regra roda em milissegundos.
/// </summary>
public sealed class FakeUnitOfWork : IUnitOfWork
{
    public bool Committed { get; private set; }
    public bool Disposed { get; private set; }

    public Task CommitAsync(CancellationToken ct = default)
    {
        Committed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeUnitOfWorkFactory : IUnitOfWorkFactory
{
    public List<FakeUnitOfWork> Created { get; } = [];

    /// <summary>Quantas transacoes foram efetivadas. Falta de commit e bug, nao detalhe.</summary>
    public int CommitCount => Created.Count(u => u.Committed);

    public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var uow = new FakeUnitOfWork();
        Created.Add(uow);
        return Task.FromResult<IUnitOfWork>(uow);
    }
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>Ids previsiveis: o teste consegue afirmar qual id foi usado onde.</summary>
public sealed class SequentialIdGenerator : IIdentifierGenerator
{
    private int _next;

    public List<Guid> Issued { get; } = [];

    public Guid NewId()
    {
        var id = new Guid($"00000000-0000-0000-0000-{++_next:D12}");
        Issued.Add(id);
        return id;
    }
}

public sealed class FakeAccountRepository : IAccountRepository
{
    public Dictionary<Guid, Account> Accounts { get; } = [];
    public List<(Guid Id, AccountStatus From, AccountStatus To)> Transitions { get; } = [];

    public Account Add(
        AccountStatus status = AccountStatus.Discovered,
        string? segment = null,
        string? domain = null)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Name = "Grupo Vento Sul",
            NormalizedName = "GRUPO VENTO SUL",
            Status = status,
            Segment = segment,
            Domain = domain
        };

        Accounts[account.Id] = account;
        return account;
    }

    public Task<Account?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Accounts.TryGetValue(id, out var a) ? a : null);

    public Task<Account?> GetByCnpjAsync(string cnpj, CancellationToken ct = default) =>
        Task.FromResult<Account?>(null);

    public Task<Guid> CreateFromCnpjAsync(
        string cnpj, string name, string? razaoSocial, string? uf, string? municipio,
        CancellationToken ct = default)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = NameNormalizer.Normalize(name),
            Status = AccountStatus.Discovered,
            State = uf,
            City = municipio
        };

        Accounts[account.Id] = account;
        return Task.FromResult(account.Id);
    }

    public Task TransitionAsync(
        IUnitOfWork uow, Guid accountId, AccountStatus from, AccountStatus to, CancellationToken ct = default)
    {
        AccountStatusTransitions.EnsureCanTransition(from, to);
        Transitions.Add((accountId, from, to));

        if (Accounts.TryGetValue(accountId, out var account))
        {
            Accounts[accountId] = account with { Status = to };
        }

        return Task.CompletedTask;
    }

    public Task<AccountContext?> GetContextAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Accounts.TryGetValue(id, out var a)
            ? new AccountContext
            {
                AccountId = a.Id,
                Name = a.Name,
                Status = a.Status.ToDbValue(),
                // O auditor deriva a URL daqui quando o evento nao traz uma.
                Domain = a.Domain,
                Segment = a.Segment
            }
            : null);
}

public sealed class FakeResearchRunRepository : IResearchRunRepository
{
    public List<Guid> Created { get; } = [];
    public ResearchRun? LatestInMonth { get; set; }

    public Task CreateAsync(
        IUnitOfWork uow, Guid runId, Guid accountId, string runType, CancellationToken ct = default)
    {
        Created.Add(runId);
        return Task.CompletedTask;
    }

    public Task<ResearchRun?> GetAsync(Guid runId, CancellationToken ct = default) =>
        Task.FromResult<ResearchRun?>(null);

    public Task CompleteAsync(
        IUnitOfWork uow, Guid runId, decimal completeness, string resultJson, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task FailAsync(Guid runId, string errorJson, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ResearchRun?> LatestSuccessfulInMonthAsync(
        Guid accountId, DateTimeOffset reference, CancellationToken ct = default) =>
        Task.FromResult(LatestInMonth);
}

public sealed class FakeOutboxRepository : IOutboxRepository
{
    public List<OutboxEvent> Enqueued { get; } = [];
    public List<Guid> Processed { get; } = [];

    public Task<Guid> EnqueueAsync(IUnitOfWork uow, OutboxEvent evt, CancellationToken ct = default)
    {
        // Idempotencia real do banco: chave repetida e no-op, nao excecao.
        if (Enqueued.Any(e => e.IdempotencyKey == evt.IdempotencyKey)) return Task.FromResult(Guid.Empty);

        Enqueued.Add(evt);
        return Task.FromResult(evt.Id);
    }

    public Task<IReadOnlyList<OutboxEvent>> ClaimBatchAsync(int batchSize, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxEvent>>([]);

    public Task MarkProcessedAsync(IUnitOfWork uow, Guid eventId, CancellationToken ct = default)
    {
        Processed.Add(eventId);
        return Task.CompletedTask;
    }

    public Task RescheduleAsync(Guid eventId, string error, int maxAttempts = 5, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public sealed class FakeAccountScoreRepository : IAccountScoreRepository
{
    public AccountScoringFacts? Facts { get; set; }
    public List<(Guid AccountId, OpportunityScore Score, string Snapshot)> Inserted { get; } = [];
    public List<(Guid AccountId, short Tier)> Tiers { get; } = [];

    public Task<AccountScoringFacts?> LoadFactsAsync(Guid accountId, CancellationToken ct = default) =>
        Task.FromResult(Facts);

    public Task InsertAsync(
        IUnitOfWork uow, Guid scoreId, Guid accountId, OpportunityScore score,
        string featureSnapshotJson, CancellationToken ct = default)
    {
        Inserted.Add((accountId, score, featureSnapshotJson));
        return Task.CompletedTask;
    }

    public Task UpdateAccountTierAsync(IUnitOfWork uow, Guid accountId, short tier, CancellationToken ct = default)
    {
        Tiers.Add((accountId, tier));
        return Task.CompletedTask;
    }

    public Task<AccountScoreView?> GetCurrentAsync(Guid accountId, CancellationToken ct = default) =>
        Task.FromResult<AccountScoreView?>(null);
}

public sealed class FakeIngestionBatchRepository : IIngestionBatchRepository
{
    public List<(Guid Id, string SourceName)> Batches { get; } = [];
    public List<(int RowNumber, RawCompanyRow Row, string ContentHash)> Rows { get; } = [];
    public List<(Guid RawId, string Status, string? Reason, Guid? AccountId)> Marks { get; } = [];
    public (int Rejected, int Created, int Attached, int Review)? Resolution { get; private set; }

    private readonly Dictionary<Guid, PendingRawCompany> _pending = [];

    public PendingRawCompany AddPending(RawCompanyRow row)
    {
        var pending = new PendingRawCompany { Id = Guid.NewGuid(), RowNumber = _pending.Count + 1, Row = row };
        _pending[pending.Id] = pending;
        return pending;
    }

    public Task<Guid> OpenAsync(
        IUnitOfWork uow, Guid batchId, string sourceName, string? sourceUri, CancellationToken ct = default)
    {
        Batches.Add((batchId, sourceName));
        return Task.FromResult(batchId);
    }

    public Task<int> AppendRowsAsync(
        IUnitOfWork uow, Guid batchId,
        IReadOnlyList<(int RowNumber, RawCompanyRow Row, string ContentHash)> rows,
        CancellationToken ct = default)
    {
        var fresh = rows.Where(r => Rows.All(existing => existing.ContentHash != r.ContentHash)).ToList();
        Rows.AddRange(fresh);
        return Task.FromResult(fresh.Count);
    }

    public Task CloseCaptureAsync(
        IUnitOfWork uow, Guid batchId, int totalRows, int acceptedRows, int duplicateRows,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<PendingRawCompany>> ListPendingAsync(
        Guid batchId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PendingRawCompany>>([.. _pending.Values.Take(limit)]);

    public Task<PendingRawCompany?> GetRawAsync(Guid rawId, CancellationToken ct = default) =>
        Task.FromResult(_pending.TryGetValue(rawId, out var raw) ? raw : null);

    public Task MarkRowAsync(
        IUnitOfWork uow, Guid rawId, string status, string? rejectionReason, Guid? accountId,
        CancellationToken ct = default)
    {
        Marks.Add((rawId, status, rejectionReason, accountId));

        // Sai da fila de pendentes; sem isso o laco de paginacao nunca termina.
        _pending.Remove(rawId);
        return Task.CompletedTask;
    }

    public Task RecordResolutionAsync(
        IUnitOfWork uow, Guid batchId, int rejected, int createdAccounts, int attachedCnpjs,
        int reviewCandidates, CancellationToken ct = default)
    {
        Resolution = (rejected, createdAccounts, attachedCnpjs, reviewCandidates);
        return Task.CompletedTask;
    }

    public Task<IngestionBatchSummary?> GetAsync(Guid batchId, CancellationToken ct = default) =>
        Task.FromResult<IngestionBatchSummary?>(null);

    public Task<IReadOnlyList<IngestionBatchSummary>> ListAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IngestionBatchSummary>>([]);
}

public sealed class FakeAccountGraphRepository : IAccountGraphRepository
{
    public List<AccountGroupCandidate> Candidates { get; } = [];
    public Dictionary<string, Guid> KnownCnpjs { get; } = [];
    public List<(Guid AccountId, NormalizedCompany Company)> Created { get; } = [];
    public List<(Guid AccountId, NormalizedCompany Company)> Attached { get; } = [];
    public List<MergeCandidateRecord> Recorded { get; } = [];
    public Dictionary<Guid, MergeCandidateView> Pending { get; } = [];
    public List<(Guid Id, bool Approved)> Decisions { get; } = [];

    public Task<IReadOnlyList<AccountGroupCandidate>> FindCandidatesAsync(
        string cnpjRoot, string normalizedName, decimal minimumSimilarity, int limit,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AccountGroupCandidate>>(Candidates);

    public Task<Guid> CreateAccountForCompanyAsync(
        IUnitOfWork uow, Guid accountId, NormalizedCompany company, decimal graphConfidence,
        CancellationToken ct = default)
    {
        Created.Add((accountId, company));
        KnownCnpjs[company.Cnpj] = accountId;
        return Task.FromResult(accountId);
    }

    public Task AttachCompanyAsync(
        IUnitOfWork uow, Guid accountId, NormalizedCompany company, CancellationToken ct = default)
    {
        Attached.Add((accountId, company));
        KnownCnpjs[company.Cnpj] = accountId;
        return Task.CompletedTask;
    }

    public Task<Guid?> FindAccountByCnpjAsync(string cnpj, CancellationToken ct = default) =>
        Task.FromResult(KnownCnpjs.TryGetValue(cnpj, out var id) ? id : (Guid?)null);

    public Task RecordMergeCandidateAsync(
        IUnitOfWork uow, Guid candidateId, MergeCandidateRecord record, CancellationToken ct = default)
    {
        Recorded.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MergeCandidateView>> ListPendingCandidatesAsync(
        int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MergeCandidateView>>([.. Pending.Values]);

    public Task<MergeCandidateView?> GetCandidateAsync(Guid candidateId, CancellationToken ct = default) =>
        Task.FromResult(Pending.TryGetValue(candidateId, out var c) ? c : null);

    public Task DecideCandidateAsync(
        IUnitOfWork uow, Guid candidateId, bool approved, string? decidedBy, CancellationToken ct = default)
    {
        Decisions.Add((candidateId, approved));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Guarda os agent_runs em memoria, separando os que entraram DENTRO da transacao
/// dos que entraram fora dela.
///
/// A separacao nao e detalhe do fake: um run que falhou grava fora de qualquer
/// transacao de negocio de proposito - o custo do modelo ja foi incorrido e o
/// motivo precisa sobreviver ao rollback. Um fake que juntasse os dois esconderia
/// exatamente a propriedade que importa.
/// </summary>
public sealed class FakeAgentRunRepository : IAgentRunRepository
{
    public List<AgentRun> InTransaction { get; } = [];
    public List<AgentRun> OutsideTransaction { get; } = [];

    public Task InsertAsync(IUnitOfWork uow, AgentRun run, CancellationToken ct = default)
    {
        InTransaction.Add(run);
        return Task.CompletedTask;
    }

    public Task InsertOutsideTransactionAsync(AgentRun run, CancellationToken ct = default)
    {
        OutsideTransaction.Add(run);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentRun>> ListAsync(Guid? accountId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AgentRun>>(
            [.. InTransaction.Concat(OutsideTransaction)]);

    public Task<decimal> TotalCostForAccountAsync(Guid accountId, CancellationToken ct = default) =>
        Task.FromResult(InTransaction.Concat(OutsideTransaction)
            .Where(r => r.AccountId == accountId)
            .Sum(r => r.EstimatedCost ?? 0m));
}
