using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Uma linha da fonte, transportada sem interpretacao. Os nomes seguem a base
/// empresarial brasileira porque e dela que o dado vem; traduzir para ingles na
/// borda so criaria um dicionario a mais para manter.
/// </summary>
public sealed record RawCompanyRow
{
    public string? Cnpj { get; init; }
    public string? RazaoSocial { get; init; }
    public string? NomeFantasia { get; init; }
    public string? CnaePrincipal { get; init; }
    public string? SituacaoCadastral { get; init; }
    public string? Municipio { get; init; }
    public string? Uf { get; init; }

    // ---------------------------------------------- so na base oficial da RFB
    // Opcionais: uma lista de CNPJs colada de planilha continua sendo entrada
    // valida, e o pipeline nao muda de forma por causa deles.
    public string? MatrizFilial { get; init; }
    public string? NaturezaJuridica { get; init; }
    public string? Porte { get; init; }
    public string? CapitalSocial { get; init; }
    public string? DataInicioAtividade { get; init; }
    public string? DataSituacaoCadastral { get; init; }
    public string? MotivoSituacaoCadastral { get; init; }
    public string? CnaesSecundarios { get; init; }
    public string? MunicipioCodigo { get; init; }
    public string? Cep { get; init; }
    public string? Logradouro { get; init; }
    public string? Numero { get; init; }
    public string? Complemento { get; init; }
    public string? Bairro { get; init; }
    public string? Telefone1 { get; init; }
    public string? Telefone2 { get; init; }
    public string? Email { get; init; }
    public string? OpcaoSimples { get; init; }
    public string? OpcaoMei { get; init; }

    public RawCompanyFields ToFields() => new()
    {
        Cnpj = Cnpj,
        RazaoSocial = RazaoSocial,
        NomeFantasia = NomeFantasia,
        CnaePrincipal = CnaePrincipal,
        SituacaoCadastral = SituacaoCadastral,
        Municipio = Municipio,
        Uf = Uf,
        MatrizFilial = MatrizFilial,
        NaturezaJuridica = NaturezaJuridica,
        Porte = Porte,
        CapitalSocial = CapitalSocial,
        DataInicioAtividade = DataInicioAtividade,
        DataSituacaoCadastral = DataSituacaoCadastral,
        MotivoSituacaoCadastral = MotivoSituacaoCadastral,
        CnaesSecundarios = CnaesSecundarios,
        MunicipioCodigo = MunicipioCodigo,
        Cep = Cep,
        Logradouro = Logradouro,
        Numero = Numero,
        Complemento = Complemento,
        Bairro = Bairro,
        Telefone1 = Telefone1,
        Telefone2 = Telefone2,
        Email = Email,
        OpcaoSimples = OpcaoSimples,
        OpcaoMei = OpcaoMei
    };
}

/// <summary>Linha crua ja persistida, aguardando normalizacao.</summary>
public sealed record PendingRawCompany
{
    public required Guid Id { get; init; }
    public required int RowNumber { get; init; }
    public required RawCompanyRow Row { get; init; }
}

public sealed record IngestionBatchSummary
{
    public required Guid Id { get; init; }
    public required string SourceName { get; init; }
    public string? SourceUri { get; init; }
    public required string Status { get; init; }
    public int TotalRows { get; init; }
    public int AcceptedRows { get; init; }
    public int DuplicateRows { get; init; }
    public int RejectedRows { get; init; }
    public int CreatedAccounts { get; init; }
    public int AttachedCnpjs { get; init; }
    public int ReviewCandidates { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>Status possiveis de <c>ingestion_batches.status</c>.</summary>
public static class IngestionBatchStatus
{
    public const string Open = "open";
    public const string Captured = "captured";
    public const string Resolved = "resolved";
    public const string Failed = "failed";
}

/// <summary>Status possiveis de <c>companies_raw.status</c>.</summary>
public static class RawCompanyStatus
{
    public const string Pending = "pending";
    public const string Normalized = "normalized";
    public const string Rejected = "rejected";
    public const string Review = "review";
}

public interface IIngestionBatchRepository
{
    Task<Guid> OpenAsync(IUnitOfWork uow, Guid batchId, string sourceName, string? sourceUri, CancellationToken ct = default);

    /// <summary>
    /// Grava as linhas cruas. Devolve quantas entraram: o indice unico por
    /// (lote, hash) faz reimportar o mesmo arquivo ser no-op em vez de erro.
    /// </summary>
    Task<int> AppendRowsAsync(IUnitOfWork uow, Guid batchId, IReadOnlyList<(int RowNumber, RawCompanyRow Row, string ContentHash)> rows, CancellationToken ct = default);

    Task CloseCaptureAsync(IUnitOfWork uow, Guid batchId, int totalRows, int acceptedRows, int duplicateRows, CancellationToken ct = default);

    Task<IReadOnlyList<PendingRawCompany>> ListPendingAsync(Guid batchId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Recupera uma linha crua pelo id. Usado na decisao de merge: aprovar ou
    /// rejeitar um candidato exige renormalizar a linha de origem, e nao confiar
    /// em campos copiados para a fila de revisao.
    /// </summary>
    Task<PendingRawCompany?> GetRawAsync(Guid rawId, CancellationToken ct = default);

    Task MarkRowAsync(IUnitOfWork uow, Guid rawId, string status, string? rejectionReason, Guid? accountId, CancellationToken ct = default);

    Task RecordResolutionAsync(IUnitOfWork uow, Guid batchId, int rejected, int createdAccounts, int attachedCnpjs, int reviewCandidates, CancellationToken ct = default);

    Task<IngestionBatchSummary?> GetAsync(Guid batchId, CancellationToken ct = default);

    Task<IReadOnlyList<IngestionBatchSummary>> ListAsync(int limit, CancellationToken ct = default);
}
