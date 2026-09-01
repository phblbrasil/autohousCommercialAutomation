using AutoHous.Revenue.Application;

namespace AutoHous.Revenue.Api;

/// <summary>
/// Adaptador de entrada HTTP. Cada handler faz apenas as cinco coisas do §12 da
/// skill de arquitetura: valida formato, converte para um comando interno,
/// invoca o caso de uso, e traduz o resultado em resposta do protocolo.
///
/// Nenhuma regra de negocio e nenhum SQL vivem aqui. A versao anterior deste
/// arquivo continha suppression, cooldown, validacao de transicao e uma consulta
/// Dapper montada dentro do lambda.
/// </summary>
public static class RevenueEndpoints
{
    public static void MapRevenueEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (IDatabaseHealthProbe probe, CancellationToken ct) =>
        {
            var reachable = await probe.IsReachableAsync(ct);

            return reachable
                ? Results.Ok(new { status = "ok", database = "reachable" })
                : Results.Problem(
                    title: "Banco indisponivel",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        MapAccountEndpoints(app);
        MapIngestionEndpoints(app);
        MapSearchEndpoints(app);
        MapAuditEndpoints(app);
    }

    // -------------------------------------------------------------- accounts

    private static void MapAccountEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/accounts", async (
            CreateAccountRequest request, CreateAccountUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(new CreateAccountCommand
            {
                Cnpj = request.Cnpj,
                Name = request.Name,
                RazaoSocial = request.RazaoSocial,
                Uf = request.Uf,
                Municipio = request.Municipio
            }, ct);

            return result.Outcome switch
            {
                CreateAccountOutcome.Created =>
                    Results.Created(
                        $"/accounts/{result.AccountId}",
                        new { account_id = result.AccountId, cnpj = result.Cnpj }),

                CreateAccountOutcome.InvalidCnpj =>
                    Results.Problem(
                        title: "CNPJ invalido",
                        detail: result.Detail,
                        statusCode: StatusCodes.Status400BadRequest),

                CreateAccountOutcome.MissingName =>
                    Results.Problem(
                        title: "Nome obrigatorio",
                        detail: result.Detail,
                        statusCode: StatusCodes.Status400BadRequest),

                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
            };
        });

        app.MapGet("/accounts/{id:guid}", async (
            Guid id, IAccountRepository accounts, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(id, ct);
            return account is null ? Results.NotFound() : Results.Ok(account);
        });

        // Projecao unica, compartilhada com a ferramenta MCP get_account_context.
        app.MapGet("/accounts/{id:guid}/context", async (
            Guid id, IAccountRepository accounts, CancellationToken ct) =>
        {
            var context = await accounts.GetContextAsync(id, ct);
            return context is null ? Results.NotFound() : Results.Ok(context);
        });

        app.MapGet("/accounts/{id:guid}/evidence", async (
            Guid id, IEvidenceReadRepository evidence, CancellationToken ct) =>
            Results.Ok(await evidence.ListForAccountAsync(id, ct)));

        // ------------------------------------------- POST /accounts/{id}/research

        app.MapPost("/accounts/{id:guid}/research", async (
            Guid id, bool? force, RequestAccountResearchUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(id, force == true, ct);

            return result.Outcome switch
            {
                RequestResearchOutcome.Accepted =>
                    Results.Accepted(
                        $"/research-runs/{result.ResearchRunId}",
                        new { research_run_id = result.ResearchRunId, status = "queued" }),

                RequestResearchOutcome.AccountNotFound => Results.NotFound(),

                RequestResearchOutcome.AccountSuppressed =>
                    Conflict("Conta suprimida", result.Detail),

                RequestResearchOutcome.ResearchInFlight =>
                    Conflict("Pesquisa em andamento",
                        $"{result.Detail} Use ?force=true para enfileirar outro."),

                RequestResearchOutcome.CooldownActive =>
                    Conflict("Pesquisa recente ja existe",
                        $"{result.Detail} Use ?force=true para repesquisar."),

                RequestResearchOutcome.InvalidTransition =>
                    Conflict("Transicao invalida", result.Detail),

                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
            };
        });

        // ---------------------------------------------- POST /accounts/{id}/audit

        // Auditoria de site (A03). Sem ?force: diferente da pesquisa, auditoria
        // nao tem cooldown mensal - o site muda quando a empresa faz replatform,
        // e represar a auditoria esconderia justamente o sinal de compra que ela
        // existe para pegar. Ver RequestWebsiteAuditUseCase.
        app.MapPost("/accounts/{id:guid}/audit", async (
            Guid id, AuditRequest? body, RequestWebsiteAuditUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(id, body?.Url, ct);

            return result.Outcome switch
            {
                RequestAuditOutcome.Accepted =>
                    Results.Accepted(
                        $"/research-runs/{result.ResearchRunId}",
                        new { research_run_id = result.ResearchRunId, status = "queued" }),

                RequestAuditOutcome.AccountNotFound => Results.NotFound(),

                RequestAuditOutcome.AccountSuppressed =>
                    Conflict("Conta suprimida", result.Detail),

                RequestAuditOutcome.MissingDomain =>
                    Conflict("Conta sem dominio", result.Detail),

                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
            };
        });

        // Candidatos a merge de grupo economico (§11). Ordena por similaridade de
        // trigrama; a decisao de merge continua sendo humana ou deterministica.
        app.MapGet("/accounts/{id:guid}/similar", async (
            Guid id, decimal? threshold, int? limit, ISearchRepository search, CancellationToken ct) =>
            Results.Ok(await search.FindSimilarAccountsAsync(
                id, Math.Clamp(threshold ?? 0.5m, 0.1m, 1.0m), Math.Clamp(limit ?? 20, 1, 100), ct)));

        // "Custo de IA por conta pesquisada": metrica auxiliar do §1 e o principal
        // criterio para decidir escalar de 1 para 10, 30, 100 contas.
        app.MapGet("/accounts/{id:guid}/cost", async (
            Guid id, IAgentRunRepository agentRuns, CancellationToken ct) =>
            Results.Ok(new
            {
                account_id = id,
                total_estimated_cost = await agentRuns.TotalCostForAccountAsync(id, ct)
            }));
    }

    // ------------------------------------------------------------- ingestao

    private static void MapIngestionEndpoints(IEndpointRouteBuilder app)
    {
        // Captura das infos: etapas 01-02 do frame 04 da V2. O corpo carrega as
        // linhas cruas; o que e ou nao aproveitavel e decisao do caso de uso.
        app.MapPost("/ingestion/batches", async (
            IngestBatchRequest request, IngestCompanyBatchUseCase useCase, CancellationToken ct) =>
        {
            if (request.Rows.Count == 0)
            {
                return Results.Problem(
                    title: "Lote vazio",
                    detail: "Informe ao menos uma linha em 'rows'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await useCase.ExecuteAsync(new IngestCompanyBatchCommand
            {
                SourceName = request.SourceName,
                SourceUri = request.SourceUri,
                Rows = request.Rows
            }, ct);

            return Results.Created($"/ingestion/batches/{result.BatchId}", result);
        });

        app.MapGet("/ingestion/batches", async (
            int? limit, IIngestionBatchRepository batches, CancellationToken ct) =>
            Results.Ok(await batches.ListAsync(Math.Clamp(limit ?? 20, 1, 100), ct)));

        app.MapGet("/ingestion/batches/{id:guid}", async (
            Guid id, IIngestionBatchRepository batches, CancellationToken ct) =>
        {
            var batch = await batches.GetAsync(id, ct);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        });

        // Etapa 03: resolucao de grupo economico. Sincrona e sob demanda porque e
        // determinstica e barata - nao ha agente envolvido.
        app.MapPost("/ingestion/batches/{id:guid}/resolve", async (
            Guid id, ResolveAccountGraphUseCase useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ExecuteAsync(id, ct)));

        // Fila de revisao do quality gate "Account confidence >= 0.80".
        app.MapGet("/merge-candidates", async (
            int? limit, IAccountGraphRepository graph, CancellationToken ct) =>
            Results.Ok(await graph.ListPendingCandidatesAsync(Math.Clamp(limit ?? 50, 1, 200), ct)));

        app.MapPost("/merge-candidates/{id:guid}/decide", async (
            Guid id, MergeDecisionRequest request, DecideMergeCandidateUseCase useCase,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(id, request.Approve, request.DecidedBy, ct);

            return result switch
            {
                MergeDecisionOutcome.Merged => Results.Ok(new { status = "merged" }),
                MergeDecisionOutcome.Rejected => Results.Ok(new { status = "rejected" }),
                MergeDecisionOutcome.NotFound => Results.NotFound(),
                MergeDecisionOutcome.AlreadyDecided =>
                    Conflict("Candidato ja decidido", "Este candidato saiu da fila de revisao."),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
            };
        });
    }

    // --------------------------------------------------------------- busca

    private static void MapSearchEndpoints(IEndpointRouteBuilder app)
    {
        // Full-text em portugues (migration 0011). A sintaxe aceita e a que o
        // usuario ja conhece: aspas para frase exata, OR para alternativa e "-"
        // para excluir termo.

        app.MapGet("/search/accounts", async (
            string? q, int? limit, ISearchRepository search, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? MissingQuery()
                : Results.Ok(await search.SearchAccountsAsync(q, Math.Clamp(limit ?? 20, 1, 100), ct)));

        app.MapGet("/search/evidence", async (
            string? q, int? limit, ISearchRepository search, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? MissingQuery()
                : Results.Ok(await search.SearchEvidenceAsync(q, Math.Clamp(limit ?? 20, 1, 100), ct)));
    }

    // ------------------------------------------------------------ auditoria

    private static void MapAuditEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/research-runs/{id:guid}", async (
            Guid id, IResearchRunRepository runs, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        app.MapGet("/agent-runs", async (
            Guid? accountId, int? limit, IAgentRunRepository agentRuns, CancellationToken ct) =>
            Results.Ok(await agentRuns.ListAsync(accountId, Math.Clamp(limit ?? 50, 1, 200), ct)));

        app.MapGet("/accounts/{id:guid}/score", async (
            Guid id, IAccountScoreRepository scores, CancellationToken ct) =>
        {
            var score = await scores.GetCurrentAsync(id, ct);
            return score is null ? Results.NotFound() : Results.Ok(score);
        });
    }

    private static IResult Conflict(string title, string? detail) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    private static IResult MissingQuery() =>
        Results.Problem(
            title: "Consulta obrigatoria",
            detail: "Informe o parametro 'q'.",
            statusCode: StatusCodes.Status400BadRequest);
}

public sealed record CreateAccountRequest(
    string Cnpj,
    string Name,
    string? RazaoSocial = null,
    string? Uf = null,
    string? Municipio = null);

public sealed record IngestBatchRequest(
    string SourceName,
    string? SourceUri,
    IReadOnlyList<RawCompanyRow> Rows);

public sealed record MergeDecisionRequest(bool Approve, string? DecidedBy = null);

/// <summary>
/// Corpo opcional de POST /accounts/{id}/audit. A url existe para auditar uma
/// vitrine em subdominio proprio - comum no setor, onde institucional e estoque
/// as vezes moram separados. Ausente, sai de accounts.domain.
/// </summary>
public sealed record AuditRequest(string? Url);
