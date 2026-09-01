using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoHous.Revenue.Application.Tests;

/// <summary>
/// O slice do Product Matcher com portas falsas: sem Postgres, sem Hermes e sem
/// rede.
///
/// O que mais importa provar aqui e a INVERSAO de ordem em relacao aos outros
/// agentes - a plataforma decide primeiro, o agente escreve depois - e as duas
/// consequencias que ela compra: o agente nao consegue escolher o produto
/// errado, e a aritmetica sobrevive a falha dele.
/// </summary>
public class MatchProductsUseCaseTests
{
    // ------------------------------------------------------------- as falsas

    private sealed class FakeProductFitRepository : IProductFitRepository
    {
        public ProductFitFacts? Facts { get; set; }
        public List<ProductFitView> Current { get; } = [];

        public Task<ProductFitFacts?> LoadFactsAsync(Guid accountId, CancellationToken ct = default) =>
            Task.FromResult(Facts);

        public Task<IReadOnlyList<ProductFitView>> GetCurrentAsync(Guid accountId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProductFitView>>(Current);
    }

    private sealed class FakeProductFitPersister : IProductFitPersister
    {
        public ProductFitPersistRequest? Persisted { get; private set; }

        public Task PersistAsync(ProductFitPersistRequest request, CancellationToken ct = default)
        {
            Persisted = request;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentRuntime(params string[] responses) : IAgentRuntime
    {
        private int _index;
        public string Name => "fake";
        public List<AgentRunRequest> Requests { get; } = [];
        public bool Fail { get; set; }

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            if (Fail) return Task.FromResult(AgentRunResult.Failure("runtime indisponivel"));

            var raw = responses.Length == 0
                ? string.Empty
                : responses[Math.Min(_index, responses.Length - 1)];

            _index++;

            return Task.FromResult(new AgentRunResult
            {
                RawText = raw,
                Succeeded = true,
                ExternalRunId = $"fake-{_index}",
                InputTokens = 100,
                OutputTokens = 50,
                EstimatedCost = 0.01m
            });
        }
    }

    private sealed class FakePromptBuilder : IProductPitchPromptBuilder
    {
        public string AgentName => "product-matcher";
        public string PromptVersion => "product-matcher-v1";

        public List<IReadOnlyList<ProductFit>> Received { get; } = [];

        public string BuildSystemPrompt() => "sistema";

        public string BuildUserPrompt(AccountContext context, IReadOnlyList<ProductFit> fits)
        {
            Received.Add(fits);
            return "usuario";
        }

        public string BuildRepairPrompt(
            AccountContext context, IReadOnlyList<ProductFit> fits, string previousOutput, string violations) =>
            "reparo";
    }

    // ------------------------------------------------------------- o cenario

    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Grupo com seis lojas, tres CNPJs e estoque em tres canais externos: a dor
    /// e de distribuicao, e MotorHub e a porta de entrada.
    ///
    /// O cenario tem chat, CRM e analytics DETECTADOS de proposito. Sem eles,
    /// AutoTalk e AutoFollow pontuam alto pela ausencia das assinaturas, e a
    /// conta deixa de ter uma porta de entrada obvia - o que faria este teste
    /// afirmar sobre um caso ambiguo em vez de sobre a regra.
    /// </summary>
    private static ProductFitFacts GrupoFragmentado(Guid accountId) => new()
    {
        AccountId = accountId,
        AccountScoreId = Guid.NewGuid(),
        Segment = "concessionaria",
        StoreCount = 6,
        InventoryEstimate = 380,
        CnpjCount = 3,
        BrandCount = 4,
        Audit = new WebsiteAuditDetail
        {
            Inventory = 0.6m, Performance = 0.5m, Seo = 0.5m, Conversion = 0.8m,
            MultiplePortals = true, PortalCount = 3, ComplexIntegration = false
        },
        Technologies =
        [
            new AccountTechnology(TechnologyCategory.Chat, "JivoChat", TechnologySource.Probe, 1m),
            new AccountTechnology(TechnologyCategory.Crm, "RD Station", TechnologySource.Probe, 1m),
            new AccountTechnology(TechnologyCategory.Analytics, "GA4", TechnologySource.Probe, 1m)
        ]
    };

    private sealed record Cenario(
        MatchProductsUseCase UseCase,
        FakeProductFitPersister Persister,
        FakeAgentRuntime Runtime,
        FakePromptBuilder Prompts,
        FakeResearchRunRepository Runs,
        FakeAgentRunRepository AgentRuns,
        Guid AccountId,
        OutboxEvent Event);

    private static Cenario Montar(ProductFitFacts facts, params string[] respostas)
    {
        var accounts = new FakeAccountRepository();
        var account = accounts.Add(AccountStatus.Scored, segment: facts.Segment, domain: "grupoventosul.com.br");

        var fits = new FakeProductFitRepository { Facts = facts with { AccountId = account.Id } };
        var persister = new FakeProductFitPersister();
        var runtime = new FakeAgentRuntime(respostas);
        var prompts = new FakePromptBuilder();
        var runs = new FakeResearchRunRepository();
        var agentRuns = new FakeAgentRunRepository();

        var runId = Guid.NewGuid();

        var evt = new OutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = EventTypes.MatchRequested,
            AggregateType = "account",
            AggregateId = account.Id,
            PayloadJson = $$"""{"account_id":"{{account.Id}}","research_run_id":"{{runId}}"}""",
            IdempotencyKey = "match:teste",
            Status = OutboxStatus.Pending
        };

        var useCase = new MatchProductsUseCase(
            accounts, fits, runs, agentRuns, persister, runtime,
            new PassthroughValidator(), prompts,
            new FixedClock(Now), new SequentialIdGenerator(),
            NullLogger<MatchProductsUseCase>.Instance);

        return new Cenario(useCase, persister, runtime, prompts, runs, agentRuns, account.Id, evt);
    }

    /// <summary>
    /// Valida de verdade contra o schema real seria testar o validador, que ja
    /// tem suite propria. Aqui interessa o FLUXO do caso de uso, entao a porta
    /// desserializa direto - e o guard continua rodando, porque e ele que o caso
    /// de uso consulta para decidir se repara.
    /// </summary>
    private sealed class PassthroughValidator : IStructuredOutputValidator
    {
        public ValidationOutcome<T> Validate<T>(string? rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return ValidationOutcome<T>.Fail(new SchemaViolation(string.Empty, "vazio"));
            }

            try
            {
                var value = System.Text.Json.JsonSerializer.Deserialize<T>(rawText);

                return value is null
                    ? ValidationOutcome<T>.Fail(new SchemaViolation(string.Empty, "nulo"))
                    : ValidationOutcome<T>.Ok(value);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return ValidationOutcome<T>.Fail(new SchemaViolation(string.Empty, ex.Message));
            }
        }
    }

    // ------------------------------------------------------------ os payloads

    private static string Pitch(string produto, int evidenceIndex = 0, string? persona = null) =>
        $$"""
        {
          "summary": "Grupo com seis unidades publicando o mesmo estoque em tres canais externos.",
          "evidence": [
            {
              "claim_type": "inventory_divergence",
              "claim_text": "Site mostra 380 veiculos; a Webmotors mostra 344 no mesmo dia.",
              "confidence": 0.85,
              "source": {
                "type": "marketplace",
                "url": "https://www.webmotors.com.br/loja/grupo-vento-sul",
                "observed_at": "2026-08-31T14:32:00-03:00"
              }
            }
          ],
          "pitches": [
            {
              "product": "{{produto}}",
              "angle": "Voces publicam o mesmo estoque em tres lugares e as contagens nao batem.",
              "reasons": [
                { "text": "Cada alteracao de preco acontece tres vezes a mao.", "evidence_index": {{evidenceIndex}} }
              ],
              "objections": [],
              "recommended_personas": [{{(persona is null ? "" : $"\"{persona}\"")}}],
              "confidence": 0.85
            }
          ],
          "disqualifiers": []
        }
        """;

    // --------------------------------------------------------------- o fluxo

    /// <summary>
    /// A aritmetica roda ANTES do agente, e o que o agente recebe e o resultado
    /// dela. Se essa ordem inverter, o agente passa a escolher o produto - e o
    /// ADR-0005 deixa de valer sem que nada quebre visivelmente.
    /// </summary>
    [Fact]
    public async Task Plataforma_decide_o_produto_antes_de_chamar_o_agente()
    {
        var c = Montar(GrupoFragmentado(Guid.Empty), Pitch(ProductCatalog.MotorHub));

        await c.UseCase.ExecuteAsync(c.Event);

        var recebido = Assert.Single(c.Prompts.Received);

        Assert.Contains(recebido, f => f.RecommendedEntry);
        Assert.Equal(ProductCatalog.MotorHub, recebido.First(f => f.RecommendedEntry).Product);

        // O agente recebeu criterios com pontos, e nao uma pergunta em aberto.
        Assert.All(recebido, f => Assert.NotEmpty(f.Reasons));
    }

    /// <summary>
    /// Pedir argumento para os cinco produtos gastaria contexto escrevendo a
    /// defesa de um BoxTech que pontuou 12 - texto que o SDR nunca usaria, e que
    /// pareceria autoritativo o bastante para alguem usar mesmo assim.
    /// </summary>
    [Fact]
    public async Task Apenas_os_produtos_que_valem_argumento_vao_para_o_agente()
    {
        var c = Montar(GrupoFragmentado(Guid.Empty), Pitch(ProductCatalog.MotorHub));

        await c.UseCase.ExecuteAsync(c.Event);

        var recebido = Assert.Single(c.Prompts.Received);

        Assert.True(recebido.Count < 5);
        Assert.All(recebido, f => Assert.True(f.Score >= ProductFitScoring.EntryThreshold));
    }

    /// <summary>
    /// As cinco notas sao gravadas, e nao so as que viraram argumento. A nota de
    /// um produto NAO escolhido e o que responde "por que nao ofereceram
    /// AutoTalk?" - e omiti-la faria a safra registrar so a conclusao, sem o
    /// caminho ate ela.
    /// </summary>
    [Fact]
    public async Task Todas_as_notas_sao_gravadas_e_nao_so_as_do_argumento()
    {
        var c = Montar(GrupoFragmentado(Guid.Empty), Pitch(ProductCatalog.MotorHub));

        await c.UseCase.ExecuteAsync(c.Event);

        Assert.NotNull(c.Persister.Persisted);
        var persisted = c.Persister.Persisted!;

        Assert.Equal(ProductCatalog.Sellable.Count, persisted.Fits.Count);
        Assert.Single(persisted.Fits, f => f.RecommendedEntry);
    }

    /// <summary>
    /// A consequencia pratica da inversao de ordem: agente fora do ar nao perde
    /// a etapa. A fila continua priorizada; falta so a frase, e ela pode ser
    /// escrita depois.
    ///
    /// E o unico dos quatro agentes com essa propriedade, e ela existe porque
    /// aqui a metade valiosa - a nota - nao depende do modelo.
    /// </summary>
    [Fact]
    public async Task Nenhum_produto_acima_do_corte_grava_a_aritmetica_sem_chamar_o_agente()
    {
        // Revenda pequena sem auditoria: nada alcanca o corte.
        var c = Montar(new ProductFitFacts
        {
            AccountId = Guid.Empty,
            Segment = "revenda",
            StoreCount = 1,
            CnpjCount = 1
        });

        await c.UseCase.ExecuteAsync(c.Event);

        Assert.Empty(c.Runtime.Requests);

        Assert.NotNull(c.Persister.Persisted);
        var persisted = c.Persister.Persisted!;

        Assert.Null(persisted.Pitch);
        Assert.Equal(ProductCatalog.Sellable.Count, persisted.Fits.Count);
        Assert.DoesNotContain(persisted.Fits, f => f.RecommendedEntry);
    }

    /// <summary>
    /// Um pitch de produto que a plataforma nao pediu nao tem nota calculada, e
    /// uma linha de <c>product_fit</c> sem nota nao entra na fila. Descartar
    /// aqui e melhor que gravar uma linha orfa: o argumento existiria no banco,
    /// pareceria valido, e nunca seria ordenado contra os outros.
    /// </summary>
    [Fact]
    public async Task Pitch_de_produto_nao_solicitado_e_descartado()
    {
        // A conta e um grupo fragmentado: AutoTalk nao entra no recorte.
        var c = Montar(GrupoFragmentado(Guid.Empty), Pitch(ProductCatalog.AutoTalk));

        await c.UseCase.ExecuteAsync(c.Event);

        Assert.NotNull(c.Persister.Persisted);
        var persisted = c.Persister.Persisted!;

        Assert.NotNull(persisted.Pitch);
        Assert.Empty(persisted.Pitch!.Pitches);

        // A aritmetica sobreviveu ao descarte.
        Assert.Equal(ProductCatalog.Sellable.Count, persisted.Fits.Count);
    }

    /// <summary>
    /// Persona fora do catalogo do produto e violacao do guard, e dispara o
    /// ciclo de reparo. A regra existe porque a persona vira criterio de busca
    /// de pessoa uma etapa adiante: um cargo inventado faz a busca voltar vazia
    /// sem ninguem saber por que.
    /// </summary>
    [Fact]
    public async Task Persona_inventada_dispara_o_ciclo_de_reparo()
    {
        var c = Montar(
            GrupoFragmentado(Guid.Empty),
            Pitch(ProductCatalog.MotorHub, persona: "Dono do grupo"),
            Pitch(ProductCatalog.MotorHub, persona: "Diretor de Operacoes"));

        await c.UseCase.ExecuteAsync(c.Event);

        Assert.Equal(2, c.Runtime.Requests.Count);
        Assert.Equal("1", c.Runtime.Requests[1].Metadata["repair_attempt"]);

        Assert.NotNull(c.Persister.Persisted);
        var persisted = c.Persister.Persisted!;
        Assert.Single(persisted.Pitch!.Pitches);
    }

    /// <summary>
    /// Reparo que tambem falha encerra o run com o motivo, e o agent_run e
    /// gravado FORA de transacao: o custo do modelo ja foi incorrido, e o motivo
    /// precisa sobreviver ao rollback.
    /// </summary>
    [Fact]
    public async Task Reparo_que_falha_encerra_o_run_sem_escrita_parcial()
    {
        var c = Montar(
            GrupoFragmentado(Guid.Empty),
            Pitch(ProductCatalog.MotorHub, evidenceIndex: 9),
            Pitch(ProductCatalog.MotorHub, evidenceIndex: 9));

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.UseCase.ExecuteAsync(c.Event));

        Assert.Null(c.Persister.Persisted);

        var registrado = c.AgentRuns.OutsideTransaction.Single();

        Assert.Equal(RunStatus.Failed, registrado.Status);
        Assert.Contains("contract_violation", registrado.ErrorJson);

        // O custo das DUAS passadas foi somado antes de falhar.
        Assert.Equal(0.02m, registrado.EstimatedCost);
    }

    [Fact]
    public async Task Runtime_indisponivel_falha_o_run_e_nao_persiste()
    {
        var c = Montar(GrupoFragmentado(Guid.Empty), Pitch(ProductCatalog.MotorHub));
        c.Runtime.Fail = true;

        await Assert.ThrowsAsync<AgentRuntimeException>(() => c.UseCase.ExecuteAsync(c.Event));

        Assert.Null(c.Persister.Persisted);
        Assert.Single(c.AgentRuns.OutsideTransaction);
    }
}
