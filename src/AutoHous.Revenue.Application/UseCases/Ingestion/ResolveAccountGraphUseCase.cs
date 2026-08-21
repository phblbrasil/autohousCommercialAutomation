using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public sealed record ResolveAccountGraphResult
{
    public required Guid BatchId { get; init; }
    public required int Processed { get; init; }
    public required int Rejected { get; init; }
    public required int CreatedAccounts { get; init; }
    public required int AttachedCnpjs { get; init; }
    public required int ReviewCandidates { get; init; }

    /// <summary>
    /// Quality gate do frame 04: proporcao de linhas resolvidas sem intervencao
    /// humana. O board pede >= 85%.
    /// </summary>
    public decimal AutoResolvedRate =>
        Processed == 0 ? 0m : Math.Round((decimal)(CreatedAccounts + AttachedCnpjs) / Processed, 4);
}

/// <summary>
/// Etapas 02 e 03 do pipeline: normalizar cada linha crua e decidir onde ela
/// entra no account graph.
///
/// Executa uma transacao POR LINHA, e nao uma para o lote inteiro. Um lote de
/// 300 mil CNPJs em transacao unica seguraria locks por minutos e perderia todo
/// o trabalho em qualquer erro; e a unidade de consistencia do negocio aqui e a
/// empresa, nao o arquivo.
/// </summary>
public sealed class ResolveAccountGraphUseCase(
    IIngestionBatchRepository batches,
    IAccountGraphRepository graph,
    IUnitOfWorkFactory unitOfWork,
    IIdentifierGenerator ids,
    ILogger<ResolveAccountGraphUseCase> logger)
{
    /// <summary>Quantos candidatos trazer do trigrama por empresa.</summary>
    private const int CandidateLimit = 10;

    /// <summary>Tamanho da janela de leitura de linhas pendentes.</summary>
    private const int PageSize = 500;

    public async Task<ResolveAccountGraphResult> ExecuteAsync(
        Guid batchId, CancellationToken ct = default)
    {
        int processed = 0, rejected = 0, created = 0, attached = 0, review = 0;

        while (true)
        {
            var pending = await batches.ListPendingAsync(batchId, PageSize, ct);
            if (pending.Count == 0) break;

            foreach (var raw in pending)
            {
                ct.ThrowIfCancellationRequested();
                processed++;

                var normalization = CompanyNormalizer.Normalize(raw.Row.ToFields());

                if (!normalization.Accepted)
                {
                    await MarkRejectedAsync(raw.Id, normalization.ReasonLabel, ct);
                    rejected++;
                    continue;
                }

                var company = normalization.Company!;

                // Este CNPJ ja esta na base? Reprocessar um lote nao pode criar
                // conta duplicada nem enfileirar revisao de novo.
                //
                // Mas tambem nao pode ser no-op: a recarga mensal traz situacao
                // cadastral, nome fantasia e municipio atualizados, e a Receita e
                // a autoridade sobre esses campos. AttachCompanyAsync e upsert,
                // entao reanexar refresca o cadastro sem mexer no vinculo.
                var existing = await graph.FindAccountByCnpjAsync(company.Cnpj, ct);

                if (existing is { } known)
                {
                    await AttachAsync(raw.Id, known, company, ct);
                    attached++;
                    continue;
                }

                var candidates = await graph.FindCandidatesAsync(
                    company.CnpjRoot, company.NormalizedName,
                    AccountSimilarity.Probable, CandidateLimit, ct);

                var decision = AccountGroupResolver.Resolve(company, candidates);

                switch (decision.Action)
                {
                    case AccountGroupAction.AttachToExisting:
                        await AttachAsync(raw.Id, decision.AccountId!.Value, company, ct);
                        attached++;
                        break;

                    case AccountGroupAction.SendToReview:
                        await SendToReviewAsync(raw.Id, decision, company, ct);
                        review++;
                        break;

                    default:
                        await CreateAsync(raw.Id, company, decision.Confidence, ct);
                        created++;
                        break;
                }
            }
        }

        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            await batches.RecordResolutionAsync(uow, batchId, rejected, created, attached, review, ct);
            await uow.CommitAsync(ct);
        }

        var result = new ResolveAccountGraphResult
        {
            BatchId = batchId,
            Processed = processed,
            Rejected = rejected,
            CreatedAccounts = created,
            AttachedCnpjs = attached,
            ReviewCandidates = review
        };

        logger.LogInformation(
            "Grafo do lote {BatchId} resolvido: {Processed} linha(s), {Created} conta(s) nova(s), " +
            "{Attached} CNPJ(s) anexado(s), {Review} em revisao, {Rejected} rejeitada(s). " +
            "Resolucao automatica: {Rate:P1}",
            batchId, processed, created, attached, review, rejected, result.AutoResolvedRate);

        return result;
    }

    private async Task MarkRejectedAsync(Guid rawId, string reason, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);
        await batches.MarkRowAsync(uow, rawId, RawCompanyStatus.Rejected, reason, null, ct);
        await uow.CommitAsync(ct);
    }

    private async Task AttachAsync(
        Guid rawId, Guid accountId, NormalizedCompany company, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);

        await graph.AttachCompanyAsync(uow, accountId, company, ct);
        await batches.MarkRowAsync(uow, rawId, RawCompanyStatus.Normalized, null, accountId, ct);

        await uow.CommitAsync(ct);
    }

    private async Task CreateAsync(
        Guid rawId, NormalizedCompany company, decimal confidence, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);

        var accountId = ids.NewId();
        await graph.CreateAccountForCompanyAsync(uow, accountId, company, confidence, ct);
        await batches.MarkRowAsync(uow, rawId, RawCompanyStatus.Normalized, null, accountId, ct);

        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// A linha fica em <c>review</c> e nao vira conta. Deixar a conta nascer e
    /// "consertar depois" e o caminho para duas contas do mesmo grupo receberem
    /// pesquisa paga em paralelo.
    /// </summary>
    private async Task SendToReviewAsync(
        Guid rawId, AccountGroupDecision decision, NormalizedCompany company, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);

        await graph.RecordMergeCandidateAsync(uow, ids.NewId(), new MergeCandidateRecord
        {
            AccountId = decision.AccountId!.Value,
            RawId = rawId,
            IncomingCnpj = company.Cnpj,
            IncomingName = company.DisplayName,
            IncomingUf = company.Uf,
            IncomingMunicipio = company.Municipio,
            Similarity = decision.Confidence,
            Reason = decision.Reason
        }, ct);

        await batches.MarkRowAsync(uow, rawId, RawCompanyStatus.Review, decision.Reason, null, ct);

        await uow.CommitAsync(ct);
    }
}
