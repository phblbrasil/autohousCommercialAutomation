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
                case EventTypes.ResearchRequested:
                    await scope.ServiceProvider
                        .GetRequiredService<ExecuteResearchRunUseCase>()
                        .ExecuteAsync(evt, ct);
                    break;

                case EventTypes.ResearchCompleted:
                    await ScoreAsync(scope, evt, ct);
                    break;

                case EventTypes.ScoreReady:
                    // Consumidor natural e o People Finder (frame 05 da V2), que
                    // ainda nao existe. Marcar como processado evita fila entupida
                    // sem esconder que o proximo elo esta faltando.
                    await MarkProcessedAsync(scope, evt, ct);
                    logger.LogInformation(
                        "Evento {EventType} sem consumidor; marcado como processado.", evt.EventType);
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
    /// Pesquisa concluida dispara o recalculo do Opportunity Score. O caso de uso
    /// da baixa no evento de entrada dentro da mesma transacao em que grava o
    /// score - marcar aqui fora abriria a janela em que o evento consta
    /// processado e o score nao existe.
    /// </summary>
    private async Task ScoreAsync(IServiceScope scope, OutboxEvent evt, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ResearchCompletedPayload>(evt.PayloadJson)
            ?? throw new InvalidOperationException($"Payload invalido no evento {evt.Id}.");

        var result = await scope.ServiceProvider
            .GetRequiredService<ScoreAccountUseCase>()
            .ExecuteAsync(payload.AccountId, evt.Id, ct);

        if (result.Outcome != ScoreAccountOutcome.Scored)
        {
            // Conta some ou entra em suppression entre a pesquisa e o scoring:
            // nao ha o que pontuar, e insistir so gastaria tentativas.
            logger.LogWarning(
                "Scoring da conta {AccountId} nao aplicavel ({Outcome}); evento baixado.",
                payload.AccountId, result.Outcome);

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
