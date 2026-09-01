using AutoHous.Revenue.Application;
using System.Text.Json;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Infrastructure;
using AutoHous.Revenue.Worker;
using Dapper;
using Npgsql;

namespace AutoHous.Revenue.Integration.Tests;

public static class TestData
{
    /// <summary>Cria uma conta em 'discovered' e devolve o id.</summary>
    public static async Task<Guid> CreateAccountAsync(
        IAccountRepository accounts, string cnpj = "11222333000181", string name = "Grupo Vento Sul") =>
        await accounts.CreateFromCnpjAsync(cnpj, name, name, "SP", "Bauru");

    /// <summary>
    /// Enfileira um research.requested exatamente como a API faz, permitindo
    /// escolher o cenario de fixture.
    /// </summary>
    public static async Task<(Guid RunId, Guid EventId)> EnqueueResearchAsync(
        IUnitOfWorkFactory factory,
        IResearchRunRepository runs,
        IAccountRepository accounts,
        IOutboxRepository outbox,
        Guid accountId,
        AccountStatus currentStatus = AccountStatus.Discovered,
        string? scenario = null)
    {
        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        await using var uow = await factory.BeginAsync();

        await runs.CreateAsync(uow, runId, accountId, "standard");
        await accounts.TransitionAsync(uow, accountId, currentStatus, AccountStatus.Researching);

        await outbox.EnqueueAsync(uow, new OutboxEvent
        {
            Id = eventId,
            EventType = EventTypes.ResearchRequested,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                research_run_id = runId,
                depth = "standard",
                fixture_scenario = scenario
            }),
            IdempotencyKey = IdempotencyKey.ForResearch(accountId, runId),
            Status = OutboxStatus.Pending,
            AvailableAt = AvailableNow
        });

        await uow.CommitAsync();
        return (runId, eventId);
    }

    /// <summary>
    /// Drena ate a condicao valer, ou ate o limite de saltos.
    ///
    /// Substitui a contagem fixa de <c>DrainHopAsync</c> onde a cadeia deixou de
    /// ter comprimento conhecido. Com o Orchestrator (A01), "pesquisa concluida"
    /// nao significa mais "score no proximo salto": a decisao depende do estado
    /// da conta, e uma conta com dominio passa pela auditoria antes de pontuar.
    ///
    /// Testar por CONDICAO e nao por contagem e o que faz o teste continuar
    /// valendo quando a politica mudar. Um teste que afirma "dois saltos" fixa a
    /// forma da cadeia, que e justamente o que o Orchestrator existe para poder
    /// mudar sem reescrever nada.
    /// </summary>
    public static async Task DrainUntilAsync(
        OutboxDispatcher dispatcher,
        Func<Task<bool>> until,
        CancellationToken ct,
        string? connectionString = null,
        int maxHops = 12)
    {
        var trilha = new List<string>();

        for (var hop = 0; hop < maxHops; hop++)
        {
            if (await until()) return;

            if (connectionString is not null)
            {
                trilha.Add(await DescribeQueueAsync(connectionString));
            }

            if (await DrainHopAsync(dispatcher, ct) == 0) break;
        }

        if (await until()) return;

        // A mensagem carrega a TRILHA porque "a condicao nao foi satisfeita" nao
        // e diagnostico: numa cadeia decidida por estado, o que importa e qual
        // evento estava na fila a cada salto e por que o ultimo parou. Sem isso,
        // investigar exige reproduzir a falha com log ligado.
        var detalhe = trilha.Count == 0
            ? "(passe connectionString para ver a trilha da fila)"
            : string.Join(Environment.NewLine + "  ",
                trilha.Select((linha, i) => $"salto {i}: {linha}"));

        throw new InvalidOperationException(
            $"A condicao nao foi satisfeita em {maxHops} saltos da fila. " +
            "Ou a cadeia parou antes do esperado, ou o Orchestrator decidiu outra coisa." +
            Environment.NewLine + "  " + detalhe);
    }

    /// <summary>Eventos pendentes e o ultimo erro de cada um, em uma linha.</summary>
    private static async Task<string> DescribeQueueAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var linhas = await connection.QueryAsync<QueueRow>(
            """
            select event_type as EventType, status as Status,
                   attempts as Attempts, last_error as LastError
              from events_outbox
             where status <> 'processed'
             order by available_at
             limit 5
            """);

        var lista = linhas.ToList();

        return lista.Count == 0
            ? "fila vazia"
            : string.Join(" | ", lista.Select(l =>
                $"{l.EventType} [{l.Status}, tentativas={l.Attempts}]" +
                (string.IsNullOrWhiteSpace(l.LastError) ? "" : $" erro: {l.LastError}")));
    }

    private sealed record QueueRow
    {
        public string EventType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int Attempts { get; init; }
        public string? LastError { get; init; }
    }

    /// <summary>
    /// Drena UM salto da cadeia de eventos, esperando o evento aparecer.
    ///
    /// Cada salto enfileira o proximo evento com o relogio da APLICACAO, e a
    /// disponibilidade e julgada pelo relogio do BANCO (ver <see cref="AvailableNow"/>).
    /// Encadear dois DrainOnce cruos assume que os dois relogios concordam ao
    /// milissegundo: quando o banco esta alguns decimos de segundo atras, o
    /// segundo drain nao acha nada e o teste falha longe da causa.
    ///
    /// Esperar so o que falta e o meio-termo entre isso e um sleep fixo, que
    /// custaria o mesmo em toda execucao para cobrir o pior caso.
    /// </summary>
    public static async Task<int> DrainHopAsync(
        OutboxDispatcher dispatcher, CancellationToken ct, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        int processed;

        while ((processed = await dispatcher.DrainOnceAsync(ct)) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, ct);
        }

        return processed;
    }

    /// <summary>
    /// Momento de disponibilidade de um evento recem-enfileirado: um minuto no
    /// passado, e nao "agora".
    ///
    /// Quem enfileira e o processo de teste, mas quem decide se o evento ja pode
    /// ser reivindicado e o banco, com o relogio DELE - o ClaimBatch filtra por
    /// <c>available_at &lt;= now()</c>. Os dois relogios nao sao o mesmo: o
    /// Postgres roda em container e a VM do Docker deriva decimos de segundo em
    /// relacao ao host.
    ///
    /// Com "agora" do lado do teste, um evento enfileirado e drenado na sequencia
    /// nasce no FUTURO para o banco: o ClaimBatch nao devolve nada, o slice nao
    /// roda, e a falha aparece longe da causa - uma colecao vazia, sem erro
    /// nenhum no log. Em producao o mesmo desvio e inofensivo, porque o
    /// dispatcher volta a cada 5 segundos.
    /// </summary>
    public static DateTimeOffset AvailableNow => DateTimeOffset.UtcNow.AddMinutes(-1);

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
}
