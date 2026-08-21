using AutoHous.Revenue.Application;
using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// Garantias do outbox. Falhar aqui significa, na pratica, cobrar duas vezes pela
/// mesma pesquisa de IA ou perder trabalho silenciosamente.
/// </summary>
public class OutboxConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _services = _postgres.BuildWorkerServices();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task SeedEventsAsync(int count)
    {
        var factory = _services.GetRequiredService<IUnitOfWorkFactory>();
        var outbox = _services.GetRequiredService<IOutboxRepository>();
        var accounts = _services.GetRequiredService<IAccountRepository>();

        var accountId = await TestData.CreateAccountAsync(accounts);

        for (var i = 0; i < count; i++)
        {
            await using var uow = await factory.BeginAsync();

            await outbox.EnqueueAsync(uow, new OutboxEvent
            {
                Id = Guid.CreateVersion7(),
                EventType = "test.noop",
                AggregateType = "account",
                AggregateId = accountId,
                PayloadJson = JsonSerializer.Serialize(new { index = i }),
                IdempotencyKey = $"test:{i}",
                Status = OutboxStatus.Pending,
                AvailableAt = DateTimeOffset.UtcNow
            });

            await uow.CommitAsync();
        }
    }

    [Fact]
    public async Task Dois_workers_concorrentes_nunca_pegam_o_mesmo_evento()
    {
        const int total = 40;
        await SeedEventsAsync(total);

        var outbox = _services.GetRequiredService<IOutboxRepository>();

        // Dois "workers" disputando a mesma fila ao mesmo tempo.
        async Task<List<Guid>> DrainAsync()
        {
            var claimed = new List<Guid>();

            while (true)
            {
                var batch = await outbox.ClaimBatchAsync(5);
                if (batch.Count == 0) break;

                claimed.AddRange(batch.Select(e => e.Id));
                await Task.Yield();
            }

            return claimed;
        }

        var results = await Task.WhenAll(DrainAsync(), DrainAsync());
        var all = results.SelectMany(r => r).ToList();

        // Sem FOR UPDATE SKIP LOCKED, os dois workers pegariam eventos repetidos.
        Assert.Equal(total, all.Count);
        Assert.Equal(total, all.Distinct().Count());

        // Nenhum evento ficou para tras.
        Assert.Equal(0, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where status = 'pending'"));
    }

    [Fact]
    public async Task Chave_de_idempotencia_impede_enfileiramento_duplicado()
    {
        var factory = _services.GetRequiredService<IUnitOfWorkFactory>();
        var outbox = _services.GetRequiredService<IOutboxRepository>();
        var accountId = await TestData.CreateAccountAsync(_services.GetRequiredService<IAccountRepository>());

        OutboxEvent Build() => new()
        {
            Id = Guid.CreateVersion7(),
            EventType = "test.noop",
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = "{}",
            IdempotencyKey = "chave-repetida",
            Status = OutboxStatus.Pending,
            AvailableAt = DateTimeOffset.UtcNow
        };

        await using (var uow = await factory.BeginAsync())
        {
            Assert.NotEqual(Guid.Empty, await outbox.EnqueueAsync(uow, Build()));
            await uow.CommitAsync();
        }

        await using (var uow = await factory.BeginAsync())
        {
            // Segundo enfileiramento e no-op, nao excecao: reenviar o mesmo
            // trabalho nao pode derrubar o chamador nem duplicar execucao.
            Assert.Equal(Guid.Empty, await outbox.EnqueueAsync(uow, Build()));
            await uow.CommitAsync();
        }

        Assert.Equal(1, await TestData.ScalarAsync<long>(_postgres.ConnectionString,
            "select count(*) from events_outbox where idempotency_key = 'chave-repetida'"));
    }

    [Fact]
    public async Task Retry_usa_backoff_e_termina_em_dead_letter()
    {
        await SeedEventsAsync(1);

        var outbox = _services.GetRequiredService<IOutboxRepository>();
        var batch = await outbox.ClaimBatchAsync(1);
        var eventId = batch[0].Id;

        // Falha repetida ate esgotar as tentativas.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await outbox.RescheduleAsync(eventId, $"falha {attempt}", maxAttempts: 5);

            var status = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
                "select status from events_outbox where id = @Id", new { Id = eventId });

            if (status == OutboxStatus.Dead) break;

            // Reivindica de novo para incrementar attempts, ignorando o backoff.
            await TestData.ScalarAsync<int>(_postgres.ConnectionString,
                "update events_outbox set available_at = now() where id = @Id", new { Id = eventId });

            await outbox.ClaimBatchAsync(1);
        }

        var finalStatus = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select status from events_outbox where id = @Id", new { Id = eventId });

        Assert.Equal(OutboxStatus.Dead, finalStatus);

        // O motivo da ultima falha sobrevive para diagnostico.
        var error = await TestData.ScalarAsync<string>(_postgres.ConnectionString,
            "select last_error from events_outbox where id = @Id", new { Id = eventId });

        Assert.Contains("falha", error);
    }
}
