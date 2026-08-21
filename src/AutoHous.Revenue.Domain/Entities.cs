namespace AutoHous.Revenue.Domain;

public sealed record Account
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? NormalizedName { get; init; }
    public string? Domain { get; init; }
    public string? Segment { get; init; }
    public short? Tier { get; init; }
    public string? State { get; init; }
    public string? City { get; init; }
    public required AccountStatus Status { get; init; }
    public int? StoreCount { get; init; }
    public int? VehicleInventoryEstimate { get; init; }
    public decimal? GraphConfidence { get; init; }
    public decimal? ResearchCompleteness { get; init; }
    public DateTimeOffset? LastResearchedAt { get; init; }
    public DateTimeOffset? NextResearchAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CompanyCnpj
{
    public required Guid Id { get; init; }
    public Guid? AccountId { get; init; }
    public required string Cnpj { get; init; }
    public string? RazaoSocial { get; init; }
    public string? NomeFantasia { get; init; }
    public string? CnaePrincipal { get; init; }
    public string? SituacaoCadastral { get; init; }
    public string? Municipio { get; init; }
    public string? Uf { get; init; }
}

public sealed record Source
{
    public required Guid Id { get; init; }
    public required EvidenceType SourceType { get; init; }
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? Domain { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? ContentHash { get; init; }
}

public sealed record Evidence
{
    public required Guid Id { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? ContactId { get; init; }
    public required Guid SourceId { get; init; }
    public required string ClaimType { get; init; }
    public required string ClaimText { get; init; }
    public string? ExtractedValueJson { get; init; }
    public decimal? Confidence { get; init; }
}

public sealed record Signal
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string SignalType { get; init; }
    public required decimal Strength { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public Guid? EvidenceId { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record ResearchRun
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string RunType { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public decimal? Completeness { get; init; }
    public string? ResultJson { get; init; }
    public string? ErrorJson { get; init; }
}

public sealed record AgentRun
{
    public required Guid Id { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? ResearchRunId { get; init; }
    public required string AgentName { get; init; }
    public required string PromptVersion { get; init; }
    public string? ModelProvider { get; init; }
    public string? ModelName { get; init; }
    public string? ExternalRunId { get; init; }
    public required string Status { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public decimal? EstimatedCost { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? ErrorJson { get; init; }
}

public sealed record OutboxEvent
{
    public required Guid Id { get; init; }
    public required string EventType { get; init; }
    public required string AggregateType { get; init; }
    public required Guid AggregateId { get; init; }
    public required string PayloadJson { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Status { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset AvailableAt { get; init; }
    public string? LastError { get; init; }
}

/// <summary>Status possiveis de <c>events_outbox.status</c>.</summary>
public static class OutboxStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string Dead = "dead";
}

/// <summary>Status possiveis de <c>research_runs.status</c> e <c>agent_runs.status</c>.</summary>
public static class RunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>Tipos de evento do outbox (secao 19).</summary>
public static class EventTypes
{
    public const string ResearchRequested = "research.requested";
    public const string ResearchCompleted = "research.completed";
    public const string AccountCreated = "account.created";
    public const string ScoreReady = "score.ready";
}
