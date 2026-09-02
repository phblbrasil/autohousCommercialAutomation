using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Dapper;

namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Escreve a safra de fit. TUDO em uma transacao: sources, evidence, as linhas
/// de <c>product_fit</c>, a ligacao com as evidencias, os desqualificadores como
/// sinais negativos, o research_run, o agent_run, o evento de saida e a baixa do
/// evento de entrada.
///
/// <c>product_fit</c> e append-only como <c>account_scores</c> (ADR-0004): a
/// safra antiga fica, e <c>v_account_current_fit</c> aponta para a vigente. E o
/// que permite responder "por que ontem a entrada era MotorHub e hoje e
/// FrontCar?" - a resposta esta na diferenca entre as duas safras, e ela some se
/// a escrita for um update.
/// </summary>
public sealed class ProductFitPersister(
    IUnitOfWorkFactory unitOfWork,
    IResearchRunRepository researchRuns,
    IAgentRunRepository agentRuns,
    IOutboxRepository outbox) : IProductFitPersister
{
    private static readonly JsonSerializerOptions FitJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Por quanto tempo um desqualificador tira a conta da fila quente.
    ///
    /// Nulo - "nunca vence" - era o comportamento anterior, e o problema nao e
    /// filosofico: a view le <c>expires_at is null or expires_at &gt; now()</c>,
    /// entao o bloqueio valia para sempre e NAO existe endpoint para revisar ou
    /// limpar a linha. "Revisao humana antes de abordar", que e o que a 0017
    /// escreveu como intencao, virava exclusao definitiva por omissao.
    ///
    /// Seis meses e o mesmo horizonte em que os outros sinais deste dominio
    /// perdem forca (ver <c>ProductFitScoring.Recency</c>, que zera em um ano) e
    /// e generoso para os casos que o agente costuma achar - recuperacao
    /// judicial, encerramento, mudanca de ramo. Vencer NAO e o mesmo que
    /// aprovar: a conta volta a ser avaliada do zero, com os fatos de entao.
    ///
    /// Isto e decisao de produto, e esta aqui em vez de espalhada no SQL para
    /// ser encontravel no dia em que alguem discordar do prazo.
    ///
    /// Em dias e nao <see cref="TimeSpan"/> porque o SQL o multiplica por
    /// <c>interval '1 day'</c>: um parametro inteiro tem tipo obvio dos dois
    /// lados, e este persister nao tem teste de integracao que pegasse uma
    /// surpresa de mapeamento.
    /// </summary>
    private const int DisqualifierHorizonDays = 180;

    public async Task PersistAsync(ProductFitPersistRequest request, CancellationToken ct = default)
    {
        var pitch = request.Pitch;

        await using var uow = await unitOfWork.BeginAsync(ct);

        // 1. Fontes e evidencias. Vazio quando o agente nao rodou ou falhou - e
        //    a aritmetica ainda e gravada, porque e ela que prioriza a fila.
        var evidenceIds = pitch is null
            ? []
            : await EvidenceWriter.WriteAllAsync(uow, request.AccountId, pitch.Evidence, ct);

        var byProduct = pitch?.Pitches.ToDictionary(p => p.Product, StringComparer.OrdinalIgnoreCase)
                        ?? [];

        // 2. Uma linha por produto, com as duas metades: a aritmetica e o
        //    argumento. Os produtos sem pitch entram assim mesmo - a nota deles
        //    e o que faz a fila ter ordem, e omiti-los esconderia por que um
        //    produto NAO foi escolhido.
        foreach (var fit in request.Fits)
        {
            var fitId = Guid.CreateVersion7();
            var written = byProduct.GetValueOrDefault(fit.Product);

            await uow.Db().ExecuteAsync(new CommandDefinition("""
                insert into product_fit
                    (id, account_id, product, score, coverage, reasons, objections,
                     recommended_personas, recommended_entry, pitch_confidence,
                     model_version, account_score_id, research_run_id, agent_run_id)
                values
                    (@Id, @AccountId, @Product, @Score, @Coverage, @Reasons::jsonb, @Objections::jsonb,
                     @Personas::jsonb, @RecommendedEntry, @PitchConfidence,
                     @ModelVersion, @AccountScoreId, @ResearchRunId, @AgentRunId)
                """,
                new
                {
                    Id = fitId,
                    request.AccountId,
                    fit.Product,
                    fit.Score,
                    fit.Coverage,
                    Reasons = BuildReasons(fit, written, evidenceIds),
                    Objections = written is null
                        ? null
                        : JsonSerializer.Serialize(
                            written.Objections.Select(o => new
                            {
                                text = o.Text,
                                response = o.Response,
                                evidence_id = At(evidenceIds, o.EvidenceIndex)
                            }), FitJson),

                    // Personas restringidas pelo agente quando ele restringiu;
                    // as do catalogo quando nao. Nulo significaria "nenhuma", e
                    // o People Finder nao teria o que procurar.
                    Personas = JsonSerializer.Serialize(
                        written is { RecommendedPersonas.Count: > 0 }
                            ? written.RecommendedPersonas
                            : fit.Personas, FitJson),

                    fit.RecommendedEntry,
                    PitchConfidence = written?.Confidence,
                    ModelVersion = ProductFitScoring.Version,
                    request.AccountScoreId,
                    request.ResearchRunId,
                    AgentRunId = request.AgentRun.Id
                }, uow.Tx(), cancellationToken: ct));

            // 3. Lastro do argumento. Sem pitch nao ha evidencia a ligar: a
            //    aritmetica se sustenta nos fatos ja persistidos por quem os
            //    observou, e nao em evidencia propria.
            if (written is null) continue;

            var cited = written.Reasons.Select(r => r.EvidenceIndex)
                .Concat(written.Objections.Select(o => o.EvidenceIndex))
                .Select(index => At(evidenceIds, index))
                .OfType<Guid>()
                .Distinct();

            foreach (var evidenceId in cited)
            {
                await uow.Db().ExecuteAsync(new CommandDefinition("""
                    insert into product_fit_evidence (product_fit_id, evidence_id)
                    values (@FitId, @EvidenceId)
                    on conflict do nothing
                    """,
                    new { FitId = fitId, EvidenceId = evidenceId }, uow.Tx(), cancellationToken: ct));
            }
        }

        // 4. Desqualificadores viram sinais NEGATIVOS, e nao tabela propria: sao
        //    fatos datados com evidencia sobre a conta, que e o que `signals`
        //    guarda. Severidade `high` vale -1, o valor que a view
        //    v_account_progress le como bloqueio.
        //
        //    So quando o agente rodou: pitch nulo e ausencia de informacao nova,
        //    e ausencia de informacao nova nao revoga o que ja se sabia.
        if (pitch is not null)
        {
            // A safra nova SUBSTITUI a anterior. Sem isto, cada execucao do
            // matcher empilha mais uma copia do mesmo desqualificador - e
            // `product_fit` e append-only, entao o matcher roda de novo a cada
            // score novo. Vencer em vez de apagar preserva o historico: a linha
            // continua respondendo "o que sabiamos em agosto?".
            await uow.Db().ExecuteAsync(new CommandDefinition("""
                update signals
                   set expires_at = now()
                 where account_id = @AccountId
                   and signal_type = 'disqualifier'
                   and (expires_at is null or expires_at > now())
                """,
                new { request.AccountId }, uow.Tx(), cancellationToken: ct));

            foreach (var disqualifier in pitch.Disqualifiers)
            {
                await uow.Db().ExecuteAsync(new CommandDefinition("""
                    insert into signals
                        (id, account_id, signal_type, strength, title, description,
                         evidence_id, observed_at, expires_at)
                    values
                        (@Id, @AccountId, @SignalType, @Strength, @Title, @Description,
                         @EvidenceId, now(), now() + (@HorizonDays * interval '1 day'))
                    """,
                    new
                    {
                        Id = Guid.CreateVersion7(),
                        request.AccountId,
                        SignalType = "disqualifier",
                        Strength = disqualifier.Severity switch
                        {
                            "high" => -1m,
                            "medium" => -0.6m,
                            _ => -0.3m
                        },
                        Title = $"Desqualificador ({disqualifier.Severity})",
                        Description = disqualifier.Reason,
                        EvidenceId = At(evidenceIds, disqualifier.EvidenceIndex),
                        HorizonDays = DisqualifierHorizonDays
                    }, uow.Tx(), cancellationToken: ct));
            }
        }

        // 5. Runs. Casar produto NAO transiciona a conta: quem promove no funil
        //    e a pesquisa, o score e o Orchestrator.
        await researchRuns.CompleteAsync(
            uow, request.ResearchRunId,
            Completeness(request.Fits),
            JsonSerializer.Serialize(new { fits = request.Fits, pitch }, FitJson),
            ct);

        await agentRuns.InsertAsync(uow, request.AgentRun, ct);

        // 6. Evento de saida e baixa do de entrada, na mesma transacao.
        var entry = request.Fits.FirstOrDefault(f => f.RecommendedEntry);

        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = EventTypes.ProductsMatched,
            AggregateType = "account",
            AggregateId = request.AccountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = request.AccountId,
                research_run_id = request.ResearchRunId,
                entry_product = entry?.Product,
                entry_score = entry?.Score,
                disqualifiers = pitch?.Disqualifiers.Count ?? 0
            }),

            // Sem safra de score o fallback e o run. Acontece na conta que foi
            // casada antes de qualquer score existir - raro pela ordem do
            // Orchestrator, e a chave precisa existir de qualquer jeito.
            IdempotencyKey = IdempotencyKey.ForProductsMatched(
                request.AccountId, request.AccountScoreId ?? request.ResearchRunId),

            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        }, ct);

        await outbox.MarkProcessedAsync(uow, request.OutboxEventId, ct);

        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// O objeto de <c>product_fit.reasons</c>, com as duas metades separadas.
    ///
    /// <c>criteria</c> e a aritmetica - sempre presente, sempre reproduzivel.
    /// <c>angle</c> e <c>narrative</c> sao do agente, e ficam ausentes quando ele
    /// nao rodou. Guardar tudo num array unico faria "o que a plataforma
    /// calculou?" e "o que o modelo escreveu?" virarem a mesma pergunta - e a
    /// primeira precisa ter resposta mesmo quando a segunda falha.
    /// </summary>
    private static string BuildReasons(ProductFit fit, ProductPitch? pitch, Guid[] evidenceIds) =>
        JsonSerializer.Serialize(new
        {
            angle = pitch?.Angle,
            criteria = fit.Reasons.Select(r => new
            {
                criterion = r.Criterion,
                points = r.Points,
                max_points = r.MaxPoints,
                observed = r.Observed,
                rationale = r.Rationale
            }),
            narrative = pitch?.Reasons.Select(r => new
            {
                text = r.Text,
                evidence_id = At(evidenceIds, r.EvidenceIndex)
            })
        }, FitJson);

    /// <summary>
    /// Completude do run: a cobertura do produto de entrada, ou a maior
    /// cobertura quando nenhum passou do corte. Nao e a media das cinco -
    /// produtos que ninguem vai oferecer nao deveriam derrubar a metrica de
    /// quao bem esta conta foi diagnosticada.
    /// </summary>
    private static decimal Completeness(IReadOnlyList<ProductFit> fits) =>
        fits.FirstOrDefault(f => f.RecommendedEntry)?.Coverage
        ?? (fits.Count == 0 ? 0m : fits.Max(f => f.Coverage));

    /// <summary>
    /// Indice fora do intervalo devolve nulo em vez de estourar.
    ///
    /// O <see cref="EvidenceFirstGuard"/> ja recusou o run quando um indice
    /// aponta para evidencia inexistente, entao isto so alcanca o caminho em que
    /// o pitch e nulo e o array esta vazio. Deixar o acesso direto significaria
    /// que uma mudanca no guard viraria uma excecao no meio de uma transacao de
    /// escrita - a pior hora possivel para descobrir.
    /// </summary>
    private static Guid? At(Guid[] evidenceIds, int index) =>
        index >= 0 && index < evidenceIds.Length ? evidenceIds[index] : null;
}
