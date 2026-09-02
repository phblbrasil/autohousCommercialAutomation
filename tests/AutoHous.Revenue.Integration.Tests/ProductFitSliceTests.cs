using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Worker;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoHous.Revenue.Integration.Tests;

/// <summary>
/// As fatias do Product Matcher (A04) e do People Finder (A05) contra um
/// Postgres de verdade.
///
/// Estes dois persisters eram os UNICOS sem cobertura de integracao, e o preco
/// apareceu na primeira execucao real: `product_fit` e `contacts` ganharam FK em
/// `agent_run_id` na migration 0017, os dois persisters gravavam o `agent_run`
/// por ultimo, e o Postgres verifica FK na hora - nao no commit. O evento morreu
/// no dead-letter com 23503.
///
/// O defeito era invisivel por construcao. Os testes de aplicacao usam persister
/// falso, entao nao ha FK; o <see cref="WebsiteAuditSliceTests"/> escreve na
/// mesma ordem errada e passa, porque `website_audits.agent_run_id` e coluna
/// solta sem FK. So um banco com as migrations aplicadas reprova isso - e e por
/// isso que estes testes existem.
/// </summary>
public class ProductFitSliceTests : IAsyncLifetime
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

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    // ------------------------------------------------------------- arranjo

    /// <summary>
    /// Conta com o minimo que o Product Matcher precisa: porte que sustente uma
    /// nota, e uma safra de score para a linha de fit ancorar.
    ///
    /// O score entra por SQL e nao pelo pipeline de propósito - o que esta em
    /// teste aqui e a escrita do fit, e fazer a conta passar por pesquisa e
    /// auditoria antes deixaria a falha de A04 escondida atras de duas etapas
    /// que ja tem cobertura propria.
    /// </summary>
    private async Task<(Guid AccountId, Guid ScoreId)> ArrangeAccountAsync()
    {
        var accountId = await TestData.CreateAccountAsync(Get<IAccountRepository>());

        await ExecuteAsync("""
            update accounts
               set domain = 'grupoventosul.com.br',
                   segment = 'multimarca',
                   store_count = 6,
                   vehicle_inventory_estimate = 380
             where id = @Id
            """, new { Id = accountId });

        // Duas marcas: alimentam o criterio `marcas` do MotorHub.
        await ExecuteAsync("""
            insert into account_brands (id, account_id, brand, relationship)
            values (gen_random_uuid(), @AccountId, 'Chevrolet', 'authorized_dealer'),
                   (gen_random_uuid(), @AccountId, 'Fiat', 'multimarca')
            """, new { AccountId = accountId });

        // Auditoria com dor real. Sem ela nenhum produto alcanca o
        // EntryThreshold: os criterios de site ficam todos nao observados, e a
        // conta nao tem porta de entrada para o teste exercitar.
        //
        // A dor e de DISTRIBUICAO (tres canais externos, integracao complexa) e
        // nao de conversa - `conversion_score` fica em 55 de proposito. O
        // desenho importa: o matcher so pede argumento para os produtos dentro
        // de 70% da nota do primeiro, e uma conta em que o AutoTalk dispara
        // deixaria o MotorHub - que e o produto que o fixture argumenta - fora
        // da faixa. O pitch seria descartado como "produto nao solicitado", e o
        // teste falharia por desequilibrio do arranjo, nao por defeito.
        await ExecuteAsync("""
            insert into website_audits
                (id, account_id, url, status, performance_score, seo_score, ux_score,
                 mobile_score, conversion_score, inventory_score, tracking_score,
                 multiple_portals, portal_count, complex_integration)
            values
                (gen_random_uuid(), @AccountId, 'https://grupoventosul.com.br', 'completed',
                 30, 35, 40, 45, 55, 60, 20, true, 3, true)
            """, new { AccountId = accountId });

        var scoreId = Guid.CreateVersion7();

        await ExecuteAsync("""
            insert into account_scores
                (id, account_id, company_fit, technology_pain, buying_signal,
                 contactability, total_score, scoring_version, feature_snapshot)
            values
                (@Id, @AccountId, 17, 21, 16, 0, 54, 'test', '{}'::jsonb)
            """, new { Id = scoreId, AccountId = accountId });

        return (accountId, scoreId);
    }

    private async Task<Guid> EnqueueAsync(
        Guid accountId, string eventType, string runType, string idempotencyKey)
    {
        var runId = Guid.CreateVersion7();

        await using var uow = await Get<IUnitOfWorkFactory>().BeginAsync();

        await Get<IResearchRunRepository>().CreateAsync(uow, runId, accountId, runType);

        await Get<IOutboxRepository>().EnqueueAsync(uow, new OutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = eventType,
            AggregateType = "account",
            AggregateId = accountId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                account_id = accountId,
                research_run_id = runId
            }),
            IdempotencyKey = idempotencyKey,
            Status = OutboxStatus.Pending,
            AvailableAt = TestData.AvailableNow
        });

        await uow.CommitAsync();
        return runId;
    }

    private Task<Guid> EnqueueMatchAsync(Guid accountId) =>
        EnqueueAsync(accountId, EventTypes.MatchRequested, "product_match",
            $"match:{accountId:N}:{Guid.NewGuid():N}");

    private Task<Guid> EnqueueContactsAsync(Guid accountId) =>
        EnqueueAsync(accountId, EventTypes.ContactsRequested, "contact_discovery",
            $"contacts:{accountId:N}:{Guid.NewGuid():N}");

    // -------------------------------------------------------------- A04

    /// <summary>
    /// A regressao que motivou o arquivo: o fit aponta para um agent_run que
    /// PRECISA existir no momento do INSERT.
    ///
    /// Se a ordem de escrita voltar a gravar o agent_run por ultimo, este teste
    /// falha com 23503 - e nao com uma tabela vazia sem explicacao, porque o
    /// <see cref="QuerySingleAsync"/> traz o erro do outbox junto.
    /// </summary>
    [Fact]
    public async Task Fit_persiste_com_agent_run_ja_gravado()
    {
        var (accountId, scoreId) = await ArrangeAccountAsync();
        var runId = await EnqueueMatchAsync(accountId);

        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var fit = await QuerySingleAsync<FitRow>("""
            select count(*)                                as Linhas,
                   count(agent_run_id)                     as ComAgentRun,
                   count(*) filter (where recommended_entry) as ComEntrada,
                   count(*) filter (where coverage is null) as SemCobertura,
                   min(account_score_id::text)             as ScoreId,
                   min(research_run_id::text)              as RunId
              from product_fit where account_id = @Id
            """, new { Id = accountId });

        // Uma linha por produto VENDAVEL: os que nao serao oferecidos entram
        // assim mesmo, porque a nota deles e o que explica por que NAO foram.
        //
        // `Sellable` e nao `All`: o Partner Program esta no catalogo publico
        // porque o agente precisa saber que existe, mas e canal, e nao produto
        // para a conta prospectada - recomenda-lo a uma concessionaria seria
        // oferecer que ela revenda a AutoHous.
        Assert.Equal(ProductCatalog.Sellable.Count, fit.Linhas);

        // O coracao da regressao. Se o FK nao fosse satisfeito, nao haveria linha
        // nenhuma - a transacao inteira teria rolado para tras.
        Assert.Equal(fit.Linhas, fit.ComAgentRun);

        Assert.Equal(scoreId.ToString(), fit.ScoreId);
        Assert.Equal(runId.ToString(), fit.RunId);
        Assert.Equal(0, fit.SemCobertura);

        // No maximo uma porta de entrada: um SDR que abre com tres produtos nao
        // abre conversa nenhuma.
        Assert.True(fit.ComEntrada <= 1);

        // E o agent_run existe de verdade, com o run de pesquisa amarrado.
        var agentRuns = await ScalarAsync<long>(
            "select count(*) from agent_runs where account_id = @Id and research_run_id = @RunId",
            new { Id = accountId, RunId = runId });

        Assert.Equal(1, agentRuns);
    }

    /// <summary>
    /// As duas metades de <c>product_fit.reasons</c>: a aritmetica da plataforma
    /// e o argumento do agente, separadas.
    ///
    /// Guardar tudo num array unico faria "o que a plataforma calculou?" e "o que
    /// o modelo escreveu?" virarem a mesma pergunta - e a primeira precisa ter
    /// resposta mesmo quando a segunda falha.
    /// </summary>
    [Fact]
    public async Task Reasons_separa_a_aritmetica_do_argumento_e_o_lastro_existe()
    {
        var (accountId, _) = await ArrangeAccountAsync();
        await EnqueueMatchAsync(accountId);

        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var criteria = await QuerySingleAsync<ReasonsRow>("""
            select bool_and(reasons ? 'criteria')                        as TemCriteria,
                   bool_or(reasons -> 'angle' <> 'null'::jsonb)          as TemAngle,
                   bool_and(jsonb_array_length(reasons -> 'criteria') > 0) as CriteriaPreenchido
              from product_fit
             where account_id = @Id
            """, new { Id = accountId });

        // A aritmetica existe em TODA linha: e ela que da ordem a fila, e sem ela
        // "por que este produto nao foi escolhido?" fica sem resposta.
        Assert.True(criteria.TemCriteria);
        Assert.True(criteria.CriteriaPreenchido);

        // O argumento existe em ao menos uma. Nao em todas de proposito: o
        // matcher so pede pitch para os produtos que valem a chamada de modelo.
        Assert.True(criteria.TemAngle);

        // Lastro pela TABELA de ligacao, e nao por array: e a divida que a 0015
        // pagou em website_audit_evidence e a 0017 repetiu aqui.
        var lastro = await ScalarAsync<long>("""
            select count(*) from product_fit_evidence e
              join product_fit f on f.id = e.product_fit_id
             where f.account_id = @Id
            """, new { Id = accountId });

        Assert.True(lastro > 0, "o pitch do fixture cita evidencia; nenhuma ligacao foi gravada");
    }

    /// <summary>
    /// Desqualificador vira sinal negativo com prazo, e a safra nova VENCE a
    /// anterior em vez de empilhar.
    ///
    /// Sem o vencimento, `expires_at` nulo faz a view ler bloqueio permanente - e
    /// nao existe endpoint para revisar a linha. Sem a substituicao, cada
    /// execucao do matcher acrescenta mais uma copia, e `product_fit` e
    /// append-only: ele roda de novo a cada score novo.
    /// </summary>
    [Fact]
    public async Task Desqualificador_tem_prazo_e_a_safra_nova_vence_a_anterior()
    {
        var (accountId, _) = await ArrangeAccountAsync();
        await EnqueueMatchAsync(accountId);
        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var primeira = await ContarDesqualificadoresAsync(accountId);

        if (primeira.Ativos == 0)
        {
            // O fixture pode nao trazer desqualificador. Nesse caso o que importa
            // e que nada foi inventado.
            Assert.Equal(0, primeira.Total);
            return;
        }

        // Nenhum ativo pode ter prazo aberto.
        Assert.Equal(0, primeira.SemPrazo);

        await EnqueueMatchAsync(accountId);
        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var segunda = await ContarDesqualificadoresAsync(accountId);

        // A segunda safra nao aumenta os ATIVOS...
        Assert.Equal(primeira.Ativos, segunda.Ativos);

        // ...mas preserva o historico: a linha vencida continua respondendo "o
        // que sabiamos antes?".
        Assert.True(segunda.Total > primeira.Total,
            "a safra anterior deveria ter sido vencida, e nao apagada");
    }

    private Task<SignalRow> ContarDesqualificadoresAsync(Guid accountId) =>
        QuerySingleAsync<SignalRow>("""
            select count(*)                                                          as Total,
                   count(*) filter (where expires_at is null or expires_at > now())   as Ativos,
                   count(*) filter (where expires_at is null)                         as SemPrazo
              from signals where account_id = @Id and signal_type = 'disqualifier'
            """, new { Id = accountId });

    // -------------------------------------------------------------- A05

    /// <summary>
    /// A mesma regressao de FK do lado dos contatos, mais o que so o banco prova:
    /// a persona classificada por NOS ao lado da sugerida pelo agente, os canais
    /// normalizados, e a evidencia separada por escopo.
    ///
    /// O People Finder depende do fit: as personas a procurar saem do produto de
    /// entrada. Por isso a fatia roda o matcher antes - e a dependencia entre as
    /// duas etapas fica exercitada de graca.
    /// </summary>
    [Fact]
    public async Task Contatos_persistem_com_agent_run_persona_e_lastro_por_escopo()
    {
        var (accountId, _) = await ArrangeAccountAsync();

        await EnqueueMatchAsync(accountId);
        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var runId = await EnqueueContactsAsync(accountId);
        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var contatos = await QuerySingleAsync<ContactRow>("""
            select count(*)                                             as Linhas,
                   count(agent_run_id)                                  as ComAgentRun,
                   count(*) filter (where seniority is not null)         as ComSenioridade,
                   count(*) filter (where confidence < 0.5)              as AbaixoDoPiso,
                   count(*) filter (where normalized_name is null)       as SemNomeNormalizado
              from contacts where account_id = @Id
            """, new { Id = accountId });

        Assert.True(contatos.Linhas > 0, "o fixture traz contatos; nenhum foi gravado");

        // A regressao: contacts.agent_run_id tem FK desde a 0017.
        Assert.Equal(contatos.Linhas, contatos.ComAgentRun);

        // O piso do ContactPolicy, imposto por check no banco.
        Assert.Equal(0, contatos.AbaixoDoPiso);
        Assert.Equal(0, contatos.SemNomeNormalizado);
        Assert.True(contatos.ComSenioridade > 0);

        var agentRuns = await ScalarAsync<long>(
            "select count(*) from agent_runs where account_id = @Id and research_run_id = @RunId",
            new { Id = accountId, RunId = runId });

        Assert.Equal(1, agentRuns);

        // A classificacao e NOSSA: `persona` sai do PersonaCatalog, `agent_persona`
        // guarda o que o modelo sugeriu. Duas colunas porque a divergencia entre
        // as duas leituras e o sinal mais barato de que a taxonomia envelheceu.
        var personas = await ScalarAsync<long>(
            "select count(*) from contacts where account_id = @Id and persona is not null and agent_persona is not null",
            new { Id = accountId });

        Assert.True(personas > 0);

        // Regra 1 desta etapa: achar o nome e achar o e-mail sao descobertas
        // diferentes, e o escopo do lastro registra isso na escrita.
        var escopos = (await QueryAsync<string>("""
            select distinct ce.claim_scope
              from contact_evidence ce join contacts c on c.id = ce.contact_id
             where c.account_id = @Id
            """, new { Id = accountId })).ToList();

        Assert.Contains("identity", escopos);
        Assert.True(escopos.Count > 1,
            "canal deveria ter escopo proprio; so 'identity' significa que o canal herdou a evidencia do contato");
    }

    /// <summary>
    /// Reexecutar a busca ATUALIZA a agenda em vez de duplica-la.
    ///
    /// E o defeito que a propria 0003 registrou ao criar o indice: "sem esta
    /// restricao o People Finder acumula duplicatas silenciosamente a cada
    /// execucao". Silenciosamente e a palavra - nada falha, a agenda so incha.
    /// </summary>
    [Fact]
    public async Task Segunda_busca_atualiza_a_agenda_em_vez_de_duplicar()
    {
        var (accountId, _) = await ArrangeAccountAsync();

        await EnqueueMatchAsync(accountId);
        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        await EnqueueContactsAsync(accountId);
        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var (contatos, canais) = await ContarAgendaAsync(accountId);

        await EnqueueContactsAsync(accountId);
        await Get<OutboxDispatcher>().DrainOnceAsync(TestContext.Current.CancellationToken);

        var (contatosDepois, canaisDepois) = await ContarAgendaAsync(accountId);

        Assert.Equal(contatos, contatosDepois);
        Assert.Equal(canais, canaisDepois);
    }

    private async Task<(long Contatos, long Canais)> ContarAgendaAsync(Guid accountId) =>
    (
        await ScalarAsync<long>(
            "select count(*) from contacts where account_id = @Id", new { Id = accountId }),
        await ScalarAsync<long>("""
            select count(*) from contact_channels ch
              join contacts c on c.id = ch.contact_id
             where c.account_id = @Id
            """, new { Id = accountId })
    );

    // ---------------------------------------------------------- auxiliares

    /// <summary>
    /// Mesma ideia do <see cref="WebsiteAuditSliceTests"/>: quando nao ha linha,
    /// contar POR QUE. O dispatcher guarda a excecao em
    /// <c>events_outbox.last_error</c> antes de reagendar, e sem trazer esse
    /// texto a falha aparece como tabela vazia - que e o sintoma, nunca a causa.
    /// Foi exatamente assim que a FK de agent_run_id se escondeu.
    /// </summary>
    private async Task<T> QuerySingleAsync<T>(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);

        var row = await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);

        if (row is not null) return row;

        var errors = (await connection.QueryAsync<string>(
            "select coalesce(last_error, '(sem erro)') from events_outbox where last_error is not null"))
            .ToList();

        throw new InvalidOperationException(
            $"A consulta nao retornou linha. Erros no outbox: [{(errors.Count > 0 ? string.Join(" | ", errors) : "nenhum")}]");
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        return await connection.QueryAsync<T>(sql, parameters);
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql, parameters);
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.ExecuteAsync(sql, parameters);
    }

    private sealed record FitRow
    {
        public long Linhas { get; init; }
        public long ComAgentRun { get; init; }
        public long ComEntrada { get; init; }
        public long SemCobertura { get; init; }
        public string? ScoreId { get; init; }
        public string? RunId { get; init; }
    }

    private sealed record ReasonsRow
    {
        public bool TemCriteria { get; init; }
        public bool TemAngle { get; init; }
        public bool CriteriaPreenchido { get; init; }
    }

    private sealed record SignalRow
    {
        public long Total { get; init; }
        public long Ativos { get; init; }
        public long SemPrazo { get; init; }
    }

    private sealed record ContactRow
    {
        public long Linhas { get; init; }
        public long ComAgentRun { get; init; }
        public long ComSenioridade { get; init; }
        public long AbaixoDoPiso { get; init; }
        public long SemNomeNormalizado { get; init; }
    }
}
