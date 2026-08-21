using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public enum MergeDecisionOutcome
{
    /// <summary>Aprovado: o CNPJ entrou na conta existente.</summary>
    Merged,

    /// <summary>Rejeitado: a empresa virou conta propria.</summary>
    Rejected,

    NotFound,
    AlreadyDecided,

    /// <summary>A linha crua sumiu ou nao normaliza mais — nada a fazer.</summary>
    SourceUnavailable
}

/// <summary>
/// Fecha um item da fila de revisao do account graph.
///
/// Rejeitar nao e descartar: se o revisor diz que a empresa NAO pertence ao
/// grupo sugerido, ela e uma conta legitima e distinta. Sem isso, toda linha
/// revisada e negada sumiria do funil — o pior resultado possivel, porque o
/// dado foi capturado, custou revisao humana, e desapareceria em silencio.
/// </summary>
public sealed class DecideMergeCandidateUseCase(
    IAccountGraphRepository graph,
    IIngestionBatchRepository batches,
    IUnitOfWorkFactory unitOfWork,
    IIdentifierGenerator ids,
    ILogger<DecideMergeCandidateUseCase> logger)
{
    public async Task<MergeDecisionOutcome> ExecuteAsync(
        Guid candidateId, bool approve, string? decidedBy, CancellationToken ct = default)
    {
        var candidate = await graph.GetCandidateAsync(candidateId, ct);

        if (candidate is null) return MergeDecisionOutcome.NotFound;
        if (candidate.Status != "pending") return MergeDecisionOutcome.AlreadyDecided;

        // Renormaliza a partir da linha de origem em vez de confiar nos campos
        // copiados para a fila: a fila guarda o suficiente para o humano decidir,
        // nao o suficiente para escrever em companies_cnpj.
        var raw = candidate.RawId is { } rawId
            ? await batches.GetRawAsync(rawId, ct)
            : null;

        if (raw is null) return MergeDecisionOutcome.SourceUnavailable;

        var normalization = CompanyNormalizer.Normalize(raw.Row.ToFields());

        if (!normalization.Accepted) return MergeDecisionOutcome.SourceUnavailable;

        var company = normalization.Company!;

        await using var uow = await unitOfWork.BeginAsync(ct);

        Guid accountId;

        if (approve)
        {
            accountId = candidate.AccountId;
            await graph.AttachCompanyAsync(uow, accountId, company, ct);
        }
        else
        {
            accountId = ids.NewId();

            // Confianca 1.00: um humano acabou de afirmar que esta conta e
            // distinta, o que e o sinal mais forte disponivel.
            await graph.CreateAccountForCompanyAsync(uow, accountId, company, 1.00m, ct);
        }

        await graph.DecideCandidateAsync(uow, candidateId, approve, decidedBy, ct);
        await batches.MarkRowAsync(uow, raw.Id, RawCompanyStatus.Normalized, null, accountId, ct);

        await uow.CommitAsync(ct);

        logger.LogInformation(
            "Candidato de merge {CandidateId} {Decision} por {DecidedBy}: CNPJ {Cnpj} -> conta {AccountId}",
            candidateId, approve ? "aprovado" : "rejeitado", decidedBy ?? "(nao informado)",
            CnpjNormalizer.Format(company.Cnpj), accountId);

        return approve ? MergeDecisionOutcome.Merged : MergeDecisionOutcome.Rejected;
    }
}
