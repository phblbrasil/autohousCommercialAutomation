using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

/// <summary>
/// O Orchestrator (A01) do lado de fora da decisao: qual COMANDO ele emite, e o
/// que ele escreve na mesma transacao.
///
/// A politica em si tem suite propria no dominio. Aqui o que se prova e o
/// efeito - que cada decisao vira o evento certo, com a chave de idempotencia
/// certa, e que o evento de entrada e baixado JUNTO com o comando de saida.
///
/// Essa ultima parte e a que mais custaria se faltasse. Baixar o evento fora da
/// transacao abriria a janela em que a conclusao consta processada e o proximo
/// passo nao foi pedido - e a conta pararia no meio do funil sem nada apontando
/// que parou.
/// </summary>
public class DecideNextActionUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeProgressRepository : IAccountProgressRepository
    {
        public AccountProgress? Progress { get; set; }

        public Task<AccountProgress?> GetAsync(Guid accountId, CancellationToken ct = default) =>
            Task.FromResult(Progress);
    }

    private sealed record Cenario(
        DecideNextActionUseCase UseCase,
        FakeOutboxRepository Outbox,
        FakeResearchRunRepository Runs,
        FakeAccountRepository Accounts,
        FakeUnitOfWorkFactory Uow,
        Guid AccountId,
        Guid SourceEventId);

    private static Cenario Montar(AccountProgress progress)
    {
        var accounts = new FakeAccountRepository();
        var account = accounts.Add(progress.Status);

        var snapshot = progress with { AccountId = account.Id };

        var repo = new FakeProgressRepository { Progress = snapshot };
        var outbox = new FakeOutboxRepository();
        var runs = new FakeResearchRunRepository();
        var uow = new FakeUnitOfWorkFactory();

        var useCase = new DecideNextActionUseCase(
            repo, accounts, runs, outbox, uow,
            new FixedClock(Now), new SequentialIdGenerator(),
            NullLogger<DecideNextActionUseCase>.Instance);

        return new Cenario(useCase, outbox, runs, accounts, uow, account.Id, Guid.NewGuid());
    }

    private static AccountProgress Pontuada(short tier = 2) => new()
    {
        AccountId = Guid.Empty,
        Status = AccountStatus.Scored,
        LastResearchedAt = Now.AddDays(-2),
        ResearchCompleteness = 0.8m,
        HasDomain = true,
        LastAuditedAt = Now.AddDays(-2),
        CurrentScoreId = Guid.NewGuid(),
        ScoredAt = Now.AddHours(-1),
        Tier = tier
    };

    // ------------------------------------------------- comando por decisao

    [Fact]
    public async Task Conta_nova_pede_pesquisa_e_cria_o_run()
    {
        var c = Montar(new AccountProgress { AccountId = Guid.Empty, Status = AccountStatus.Discovered });

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.Research, result.Action);

        var evt = Assert.Single(c.Outbox.Enqueued);
        Assert.Equal(EventTypes.ResearchRequested, evt.EventType);

        // O run existe e a chave o referencia: e o que torna a repeticao
        // detectavel pelo indice unico do outbox.
        var runId = Assert.Single(c.Runs.Created);
        Assert.Equal(IdempotencyKey.ForResearch(c.AccountId, runId), evt.IdempotencyKey);
    }

    [Fact]
    public async Task Conta_pesquisada_com_dominio_pede_auditoria()
    {
        var c = Montar(new AccountProgress
        {
            AccountId = Guid.Empty,
            Status = AccountStatus.Researched,
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.8m,
            HasDomain = true
        });

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.Audit, result.Action);
        Assert.Equal(EventTypes.AuditRequested, c.Outbox.Enqueued.Single().EventType);
        Assert.Single(c.Runs.Created);
    }

    /// <summary>
    /// Scoring nao ganha research_run: e aritmetica sobre fatos ja persistidos,
    /// nao chama modelo e nao tem custo. Uma linha em <c>research_runs</c> para
    /// cada recalculo poluiria a resposta de "por que esta conta tem cinco runs
    /// em agosto?" com execucoes que nunca custaram nada.
    /// </summary>
    [Fact]
    public async Task Pedido_de_score_nao_cria_research_run()
    {
        var c = Montar(new AccountProgress
        {
            AccountId = Guid.Empty,
            Status = AccountStatus.Researched,
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.8m,
            HasDomain = true,
            LastAuditedAt = Now.AddHours(-1)
        });

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.Score, result.Action);
        Assert.Equal(EventTypes.ScoreRequested, c.Outbox.Enqueued.Single().EventType);
        Assert.Empty(c.Runs.Created);
    }

    [Fact]
    public async Task Conta_pontuada_pede_o_fit_ancorado_na_safra_de_score()
    {
        var progress = Pontuada();
        var c = Montar(progress);

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.MatchProducts, result.Action);

        var evt = c.Outbox.Enqueued.Single();

        Assert.Equal(EventTypes.MatchRequested, evt.EventType);

        // Ancorada no SCORE e nao no tempo: refazer o fit sobre a mesma safra
        // gastaria uma chamada de modelo para reescrever o mesmo argumento.
        Assert.Equal(
            IdempotencyKey.ForMatch(c.AccountId, progress.CurrentScoreId!.Value),
            evt.IdempotencyKey);
    }

    [Fact]
    public async Task Conta_com_fit_pede_contatos_ancorados_na_safra_de_fit()
    {
        var fitId = Guid.NewGuid();

        var c = Montar(Pontuada() with
        {
            ProductFitBatchId = fitId,
            ProductFitAt = Now.AddMinutes(-30),
            HasRecommendedEntry = true
        });

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.FindContacts, result.Action);

        var evt = c.Outbox.Enqueued.Single();

        Assert.Equal(EventTypes.ContactsRequested, evt.EventType);
        Assert.Equal(IdempotencyKey.ForContacts(c.AccountId, fitId), evt.IdempotencyKey);
    }

    /// <summary>
    /// MarkReady e o unico comando que tambem PROMOVE a conta. Os outros pedem
    /// trabalho, e quem promove e o resultado do trabalho.
    /// </summary>
    [Fact]
    public async Task Conta_completa_e_promovida_para_ready()
    {
        var c = Montar(Pontuada() with
        {
            ProductFitBatchId = Guid.NewGuid(),
            ProductFitAt = Now.AddMinutes(-30),
            HasRecommendedEntry = true,
            ContactsSearchedAt = Now.AddMinutes(-10),
            HasDecisionMaker = true
        });

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.MarkReady, result.Action);
        Assert.Equal(EventTypes.AccountReady, c.Outbox.Enqueued.Single().EventType);

        var transicao = Assert.Single(c.Accounts.Transitions);
        Assert.Equal(AccountStatus.Ready, transicao.To);
    }

    // ------------------------------------------------- decisoes sem comando

    [Fact]
    public async Task Conta_suprimida_para_sem_emitir_comando()
    {
        var c = Montar(Pontuada() with { Status = AccountStatus.Suppressed });

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.Stop, result.Action);
        Assert.Empty(c.Outbox.Enqueued);
        Assert.Empty(c.Runs.Created);

        // O evento de entrada e baixado assim mesmo: reagendar giraria a fila
        // para sempre sobre uma conta que nunca vai andar.
        Assert.Contains(c.SourceEventId, c.Outbox.Processed);
    }

    [Fact]
    public async Task Run_em_voo_espera_sem_empilhar_comando()
    {
        var c = Montar(new AccountProgress
        {
            AccountId = Guid.Empty,
            Status = AccountStatus.Researching,
            HasRunInFlight = true
        });

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.Wait, result.Action);
        Assert.Empty(c.Outbox.Enqueued);
        Assert.Contains(c.SourceEventId, c.Outbox.Processed);
    }

    [Fact]
    public async Task Tier_frio_sai_da_fila_quente_com_transicao_para_nurture()
    {
        var c = Montar(Pontuada(tier: 4));

        var result = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.Nurture, result.Action);
        Assert.Empty(c.Outbox.Enqueued);

        var transicao = Assert.Single(c.Accounts.Transitions);
        Assert.Equal(AccountStatus.Nurture, transicao.To);
    }

    // ------------------------------------------------------ a transacionalidade

    /// <summary>
    /// O comando de saida e a baixa do de entrada acontecem na MESMA transacao,
    /// e ela e efetivada. Sem isso existe a janela em que a conclusao consta
    /// processada e o proximo passo nunca foi pedido.
    /// </summary>
    [Fact]
    public async Task Comando_e_baixa_saem_na_mesma_transacao_efetivada()
    {
        var c = Montar(Pontuada());

        await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Single(c.Uow.Created);
        Assert.Equal(1, c.Uow.CommitCount);

        Assert.Single(c.Outbox.Enqueued);
        Assert.Contains(c.SourceEventId, c.Outbox.Processed);
    }

    /// <summary>
    /// Conta apagada entre a conclusao e a decisao: da baixa e para. Reagendar
    /// reprocessaria para sempre um evento cujo agregado nao existe mais.
    /// </summary>
    [Fact]
    public async Task Conta_inexistente_baixa_o_evento_e_para()
    {
        var c = Montar(Pontuada());
        var repo = new FakeProgressRepository { Progress = null };

        var useCase = new DecideNextActionUseCase(
            repo, c.Accounts, c.Runs, c.Outbox, c.Uow,
            new FixedClock(Now), new SequentialIdGenerator(),
            NullLogger<DecideNextActionUseCase>.Instance);

        var result = await useCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        Assert.Equal(NextAction.Stop, result.Action);
        Assert.Empty(c.Outbox.Enqueued);
        Assert.Contains(c.SourceEventId, c.Outbox.Processed);
    }

    /// <summary>
    /// Duas execucoes sobre o mesmo retrato produzem a MESMA chave de
    /// idempotencia, e o outbox recusa a segunda. E o que impede a rajada de
    /// eventos - pesquisa concluida e auditoria concluida chegando juntas - de
    /// pedir dois fits da mesma conta.
    /// </summary>
    [Fact]
    public async Task Retrato_repetido_nao_enfileira_o_comando_duas_vezes()
    {
        var c = Montar(Pontuada());

        await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);
        await c.UseCase.ExecuteAsync(c.AccountId, Guid.NewGuid());

        Assert.Single(c.Outbox.Enqueued);
    }

    /// <summary>
    /// Comando descartado por idempotencia nao pode deixar research_run para
    /// tras. E a guarda mais cara deste arquivo se faltar.
    ///
    /// O run nasce `queued`, e `queued` e o que <c>has_run_in_flight</c> le. Um
    /// run que nenhum comando vai executar - porque o comando foi descartado -
    /// prende a conta em <c>Wait</c> em TODA decisao seguinte, e nao ha lease no
    /// claim do outbox nem varredura de run velho para desfazer. A conta some do
    /// funil sem erro, sem log e sem linha vermelha em lugar nenhum.
    ///
    /// Por isso a ordem importa: enfileirar primeiro, criar o run so depois de
    /// saber que o comando entrou.
    /// </summary>
    [Fact]
    public async Task Comando_descartado_por_idempotencia_nao_deixa_run_orfao()
    {
        var c = Montar(Pontuada());

        var primeira = await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        var segundoEvento = Guid.NewGuid();
        var segunda = await c.UseCase.ExecuteAsync(c.AccountId, segundoEvento);

        Assert.Equal(NextAction.MatchProducts, primeira.Action);
        Assert.Equal(NextAction.MatchProducts, segunda.Action);

        // Um comando, um run.
        Assert.Single(c.Outbox.Enqueued);
        Assert.Single(c.Runs.Created);

        // E o resultado nao inventa um evento que nao existe no outbox.
        Assert.NotNull(primeira.EnqueuedEventId);
        Assert.Null(segunda.EnqueuedEventId);
        Assert.Null(segunda.ResearchRunId);

        // O evento de entrada da segunda passada ainda e baixado: deixa-lo
        // pendente o faria voltar para sempre pedindo a mesma decisao.
        Assert.Contains(segundoEvento, c.Outbox.Processed);
    }

    // ------------------------------------------- contrato produtor/consumidor

    /// <summary>
    /// Todo comando que o Orchestrator emite desserializa no payload que o
    /// consumidor dele espera.
    ///
    /// Existe por causa de um defeito real. O Orchestrator emite um payload
    /// unico para todos os comandos, com <c>research_run_id</c> nulo quando a
    /// etapa nao cria run — e o scoring e exatamente essa etapa, porque
    /// aritmetica sobre fatos ja persistidos nao ganha linha em
    /// <c>research_runs</c>. O consumidor desserializava num record que exigia
    /// <c>Guid</c> nao-anulavel, e o run era reagendado ate o dead-letter com um
    /// erro de JSON que nao mencionava a causa.
    ///
    /// Nada no compilador liga as duas pontas: quem emite e quem consome so se
    /// encontram em tempo de execucao, dentro de uma transacao, num worker. Este
    /// teste e o encontro antecipado.
    /// </summary>
    [Theory]
    [InlineData(EventTypes.ResearchRequested)]
    [InlineData(EventTypes.AuditRequested)]
    [InlineData(EventTypes.ScoreRequested)]
    [InlineData(EventTypes.MatchRequested)]
    [InlineData(EventTypes.ContactsRequested)]
    public async Task Comando_emitido_desserializa_no_payload_do_consumidor(string eventType)
    {
        var c = Montar(RetratoQueProduz(eventType));

        await c.UseCase.ExecuteAsync(c.AccountId, c.SourceEventId);

        var evt = Assert.Single(c.Outbox.Enqueued);

        Assert.Equal(eventType, evt.EventType);

        // Desserializa com o MESMO record que o caso de uso consumidor usa.
        // Falhar aqui e o defeito acontecendo, com o nome do evento no lugar de
        // um "LineNumber: 0" sem contexto.
        var accountId = eventType switch
        {
            EventTypes.ResearchRequested => Deserialize<ResearchRequestedPayload>(evt).AccountId,
            EventTypes.AuditRequested => Deserialize<AuditRequestedPayload>(evt).AccountId,
            EventTypes.ScoreRequested => Deserialize<AccountEventPayload>(evt).AccountId,
            EventTypes.MatchRequested => Deserialize<MatchRequestedPayload>(evt).AccountId,
            EventTypes.ContactsRequested => Deserialize<ContactsRequestedPayload>(evt).AccountId,
            _ => throw new InvalidOperationException($"Evento sem consumidor mapeado: {eventType}")
        };

        Assert.Equal(c.AccountId, accountId);
    }

    private static T Deserialize<T>(OutboxEvent evt) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(evt.PayloadJson)
        ?? throw new InvalidOperationException($"Payload de {evt.EventType} desserializou para nulo.");

    /// <summary>O retrato mais simples que faz o Orchestrator emitir cada comando.</summary>
    private static AccountProgress RetratoQueProduz(string eventType) => eventType switch
    {
        EventTypes.ResearchRequested =>
            new AccountProgress { AccountId = Guid.Empty, Status = AccountStatus.Discovered },

        EventTypes.AuditRequested => new AccountProgress
        {
            AccountId = Guid.Empty,
            Status = AccountStatus.Researched,
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.8m,
            HasDomain = true
        },

        // Sem dominio: pula a auditoria e vai direto pontuar. E o caminho em que
        // o comando sai SEM research_run_id, que foi onde o defeito morava.
        EventTypes.ScoreRequested => new AccountProgress
        {
            AccountId = Guid.Empty,
            Status = AccountStatus.Researched,
            LastResearchedAt = Now.AddDays(-1),
            ResearchCompleteness = 0.8m,
            HasDomain = false
        },

        EventTypes.MatchRequested => Pontuada(),

        EventTypes.ContactsRequested => Pontuada() with
        {
            ProductFitBatchId = Guid.NewGuid(),
            ProductFitAt = Now.AddMinutes(-30),
            HasRecommendedEntry = true
        },

        _ => throw new InvalidOperationException($"Retrato nao definido para {eventType}.")
    };
}
