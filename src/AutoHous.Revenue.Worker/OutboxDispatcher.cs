using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Worker;

/// <summary>
/// Consome o outbox: reivindica um lote, roteia por event_type e reagenda o que
/// falhar. E o "worker" da secao 20 do blueprint.
/// </summary>
public sealed class OutboxDispatcher(
    IOutboxRepository outbox,
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger,
    OutboxDispatcherOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "OutboxDispatcher iniciado (lote={BatchSize}, intervalo={Interval}s)",
            options.BatchSize, options.PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await DrainOnceAsync(stoppingToken);

                // Só dorme quando não havia trabalho: com fila cheia, o loop
                // continua imediatamente.
                if (processed == 0)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no loop do OutboxDispatcher; aguardando antes de retomar.");
                await Task.Delay(options.PollInterval, stoppingToken);
            }
        }

        logger.LogInformation("OutboxDispatcher encerrado.");
    }

    /// <summary>
    /// Processa um lote. Exposto para os testes de integracao, que precisam de
    /// execucao deterministica em vez de esperar o loop.
    /// </summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        var batch = await outbox.ClaimBatchAsync(options.BatchSize, ct);

        foreach (var evt in batch)
        {
            await ProcessAsync(evt, ct);
        }

        return batch.Count;
    }

    private async Task ProcessAsync(OutboxEvent evt, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();

        try
        {
            switch (evt.EventType)
            {
                // ----------------------------------------------------- comandos
                // Rotear COMANDO por tipo e infraestrutura, e esta certo aqui:
                // "audit.requested vai para o auditor" nao depende do estado da
                // conta e nao e decisao de negocio.

                case EventTypes.ResearchRequested:
                    await scope.ServiceProvider
                        .GetRequiredService<ExecuteResearchRunUseCase>()
                        .ExecuteAsync(evt, ct);
                    break;

                case EventTypes.AuditRequested:
                    await scope.ServiceProvider
                        .GetRequiredService<ExecuteWebsiteAuditUseCase>()
                        .ExecuteAsync(evt, ct);
                    break;

                case EventTypes.ScoreRequested:
                    await ScoreAsync(scope, evt, ct);
                    break;

                case EventTypes.MatchRequested:
                    await scope.ServiceProvider
                        .GetRequiredService<MatchProductsUseCase>()
                        .ExecuteAsync(evt, ct);
                    break;

                case EventTypes.ContactsRequested:
                    await scope.ServiceProvider
                        .GetRequiredService<ExecutePeopleFinderUseCase>()
                        .ExecuteAsync(evt, ct);
                    break;

                // --------------------------------------------------- conclusoes
                // Todas vao para o MESMO consumidor, e essa e a mudanca que o
                // Orchestrator (A01) trouxe.
                //
                // Antes, cada conclusao tinha seu proprio destino aqui dentro:
                // research.completed pontuava, audit.completed pontuava de novo,
                // score.ready nao tinha consumidor. Aquilo era politica - "o que
                // vem depois de pesquisar?" - escrita dentro de um switch de
                // infraestrutura, e o switch so enxergava o evento que acabara
                // de chegar. Nao havia de onde perguntar "esta conta ja tem
                // auditoria?", entao a cadeia era fixa por construcao.
                //
                // Agora a conclusao diz apenas "algo mudou nesta conta". Quem
                // decide o proximo passo le o retrato inteiro.

                case EventTypes.ResearchCompleted:
                case EventTypes.AuditCompleted:
                case EventTypes.ScoreReady:
                case EventTypes.ProductsMatched:
                case EventTypes.ContactsFound:
                    await OrchestrateAsync(scope, evt, ct);
                    break;

                // account.created NAO entra na cadeia automatica, e a ausencia e
                // deliberada.
                //
                // Hoje nenhum produtor o emite - a constante existe desde a
                // secao 19 sem uso. Quem passaria a emiti-lo e o pipeline de
                // ingestao, e ele cria contas as centenas de milhares: liga-lo
                // ao Orchestrator faria uma carga nacional da Receita pedir
                // pesquisa para cada linha, o que e uma decisao de orcamento e
                // nao de arquitetura.
                //
                // Entrar no funil continua sendo ato explicito - POST
                // /accounts/{id}/research, ou um recorte de fila que alguem
                // escolhe. O dia em que houver uma politica de admissao
                // ("tier 1 e 2 do CNAE, no Sul, com site"), ela vira o
                // consumidor deste evento.
                case EventTypes.AccountCreated:
                    await MarkProcessedAsync(scope, evt, ct);
                    logger.LogInformation(
                        "Conta {AccountId} criada; entrada no funil e ato explicito.", evt.AggregateId);
                    break;

                // Fim da cadeia inbound. O consumidor e o SDR (A06), que nao
                // existe: o evento e baixado com registro explicito de que a
                // conta ficou pronta e nao ha quem a aborde.
                case EventTypes.AccountReady:
                    await MarkProcessedAsync(scope, evt, ct);
                    logger.LogInformation(
                        "Conta {AccountId} pronta para abordagem; sem SDR (A06) para consumir.",
                        evt.AggregateId);
                    break;

                default:
                    logger.LogWarning("Tipo de evento desconhecido: {EventType}", evt.EventType);
                    await outbox.RescheduleAsync(evt.Id, $"Tipo desconhecido: {evt.EventType}", options.MaxAttempts, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao processar o evento {EventId} ({EventType})", evt.Id, evt.EventType);
            await outbox.RescheduleAsync(evt.Id, ex.Message, options.MaxAttempts, ct);
        }
    }

    /// <summary>
    /// Entrega a decisao ao Orchestrator.
    ///
    /// O dispatcher le UM campo do payload - <c>account_id</c> - e nada mais. E
    /// deliberado: o Orchestrator decide pelo estado da conta no banco, e nao
    /// pelo que o evento conta. Passar o resto do payload adiante convidaria a
    /// decisao a depender de qual evento chegou, que e exatamente o acoplamento
    /// que esta reorganizacao desfez.
    ///
    /// O caso de uso da baixa no evento de entrada dentro da propria transacao
    /// em que enfileira o comando seguinte - marcar aqui fora abriria a janela
    /// em que o evento consta processado e o proximo passo nao foi pedido.
    /// </summary>
    private async Task OrchestrateAsync(IServiceScope scope, OutboxEvent evt, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<AccountEventPayload>(evt.PayloadJson);

        // AggregateId como reserva: todo evento de conclusao tem a conta no
        // agregado, e um payload malformado nao deveria parar a cadeia inteira.
        var accountId = payload?.AccountId is { } id && id != Guid.Empty
            ? id
            : evt.AggregateId;

        var decision = await scope.ServiceProvider
            .GetRequiredService<DecideNextActionUseCase>()
            .ExecuteAsync(accountId, evt.Id, ct);

        logger.LogDebug(
            "Evento {EventType} da conta {AccountId} resultou em {Action}: {Rationale}",
            evt.EventType, accountId, decision.Action, decision.Rationale);
    }

    /// <summary>
    /// Recalculo do Opportunity Score, pedido pelo Orchestrator.
    ///
    /// Le <see cref="AccountEventPayload"/>, e nao <c>ResearchCompletedPayload</c>:
    /// os dois carregam <c>account_id</c>, mas o comando de score NAO tem
    /// <c>research_run_id</c> - pontuar e aritmetica sobre fatos ja persistidos e
    /// nao ganha linha em <c>research_runs</c>. Desserializar o payload do
    /// comando num record que exige o run falha em <c>null</c>, e o dispatcher
    /// reagenda ate o dead-letter com um erro de JSON que nao menciona a causa.
    ///
    /// Ler so o que se usa e o que evita esse acoplamento: quem pontua precisa da
    /// conta, e de mais nada.
    ///
    /// O caso de uso da baixa no evento de entrada na mesma transacao em que
    /// grava o score.
    /// </summary>
    private async Task ScoreAsync(IServiceScope scope, OutboxEvent evt, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<AccountEventPayload>(evt.PayloadJson);

        var accountId = payload?.AccountId is { } id && id != Guid.Empty
            ? id
            : evt.AggregateId;

        var result = await scope.ServiceProvider
            .GetRequiredService<ScoreAccountUseCase>()
            .ExecuteAsync(accountId, evt.Id, ct);

        if (result.Outcome != ScoreAccountOutcome.Scored)
        {
            // Conta some ou entra em suppression entre a pesquisa e o scoring:
            // nao ha o que pontuar, e insistir so gastaria tentativas.
            logger.LogWarning(
                "Scoring da conta {AccountId} nao aplicavel ({Outcome}); evento baixado.",
                accountId, result.Outcome);

            await MarkProcessedAsync(scope, evt, ct);
        }
    }

    private static async Task MarkProcessedAsync(IServiceScope scope, OutboxEvent evt, CancellationToken ct)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        var repository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

        await using var uow = await factory.BeginAsync(ct);
        await repository.MarkProcessedAsync(uow, evt.Id, ct);
        await uow.CommitAsync(ct);
    }
}

public sealed class OutboxDispatcherOptions
{
    public int BatchSize { get; set; } = 10;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxAttempts { get; set; } = 5;
}
