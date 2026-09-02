# Arquitetura

## Camadas

```
Api ----------> Application -------> Domain
Worker -------> Application -------> Domain
Ingestor -----> Application -------> Domain
Infrastructure -> Application ------> Domain
Agents -------> Application -------> Domain
ReceitaFederal -> Application ------> Domain
Mcp ----------> (HTTP)  ─────────────> Revenue API
```

| Projeto | Responsabilidade |
|---|---|
| `Domain` | Entidades, políticas e regras puras. Zero `PackageReference`. |
| `Application` | Portas e casos de uso. Nunca Npgsql, HttpClient ou ASP.NET Core. |
| `Infrastructure` | Npgsql + Dapper. Implementa as portas de persistência. |
| `Agents` | Implementa `IAgentRuntime`, o validador de schema e os prompts. |
| `ReceitaFederal` | Fonte oficial de CNPJ: WebDAV, zip, ISO-8859-1 e layout posicional. Não conhece persistência. |
| `Api` | Minimal API. Adaptador de entrada + ponto de composição. |
| `Worker` | Dispatcher do outbox. Adaptador de entrada + ponto de composição. |
| `Ingestor` | CLI de captura. Adaptador de entrada + ponto de composição. |
| `Migrator` | DbUp sobre SQL puro. |
| `Mcp` | MCP read-only. Fala HTTP; não referencia Npgsql. |
| `WebAudit` | Sonda de site: HTTP e parse de HTML com AngleSharp. Não referencia Npgsql. |

As direções são impostas por
[`AutoHous.Revenue.Architecture.Tests`](../tests/AutoHous.Revenue.Architecture.Tests),
não por convenção. Ver [ADR-0001](adr/0001-camada-de-aplicacao.md).

## Fluxo: captura

```
release da Receita Federal (7,3 GB)      arquivo delimitado / POST /ingestion/batches
   │                                        │
   ├── PrepareReceitaReleaseUseCase         │
   │      Estabelecimentos → rf_cnae_stats  │   (conta os ~63M, antes de filtrar)
   │      filtro de CNAE   → spool          │
   │      + Empresas + Simples (+ Socios)   │
   │                                        │
   └── IngestCompanyStreamUseCase ──────────┴── IngestCompanyBatchUseCase
              (blocos)                                 (lista)
                     │
                     └── companies_raw (pending), sem interpretação
                                │
                     ResolveAccountGraphUseCase ── por linha, uma transação
                              CompanyNormalizer  →  rejeita com motivo, ou
                              AccountGroupResolver → conta nova | anexa CNPJ | revisão
```

As duas fontes divergem só na leitura. `companies_raw` para baixo é o mesmo
caminho — é o que impede a fonte nova de agrupar contas por uma regra diferente
da antiga.

Detalhes em [ingestion.md](ingestion.md).

## Fluxo: pesquisa

```
POST /accounts/{id}/research
   │  RequestAccountResearchUseCase
   │  valida conta, suppression, run em voo, cooldown, transição
   │
   ├── uma transação ──────────────────────────────────────────
   │   insert research_runs (queued)
   │   update accounts.status = researching
   │   insert events_outbox (research.requested)
   └───────────────────────────────────────────── 202 Accepted

Revenue Worker
   │  claim com FOR UPDATE SKIP LOCKED
   │  ExecuteResearchRunUseCase
   │  IAgentRuntime.RunAsync (hermes | fixture)
   │  StructuredOutputValidator + EvidenceFirstGuard
   │  (falhou? 1 tentativa de reparo)
   │
   ├── uma transação ──────────────────────────────────────────
   │   sources (dedup por hash) → evidence → signals
   │   → account_brands → account_locations
   │   → accounts (domain, segmento, lojas, completude, researched)
   │   → research_runs (completed) → agent_runs (tokens, custo)
   │   → events_outbox (research.completed)
   │   → evento de entrada marcado como processed
   └────────────────────────────────────────────────────────────
```

## Fluxo: scoring

```
research.completed
   │  ScoreAccountUseCase — determinístico, sem agente, custo zero
   │
   ├── uma transação ──────────────────────────────────────────
   │   account_scores (append-only, com breakdown explicável)
   │   accounts.tier
   │   accounts.status  researched → scored
   │   events_outbox (score.ready)
   │   evento de entrada marcado como processed
   └────────────────────────────────────────────────────────────
```

Detalhes em [scoring.md](scoring.md).

### A cadeia completa, depois do Orchestrator

O dispatcher deixou de decidir o que vem depois de cada conclusão. Ele roteia
**comandos** — isso é infraestrutura e continua correto — e entrega toda
**conclusão** ao mesmo consumidor, que lê o estado da conta e emite o comando
seguinte.

```
                      ┌──────────────────────────────────────┐
  conclusões  ────────►  DecideNextActionUseCase (A01)        │
  research.completed  │  lê v_account_progress numa leitura   │
  audit.completed     │  AccountOrchestration.Decide (puro)   │
  score.ready         └──────────────┬───────────────────────┘
  products.matched                   │ emite UM comando
  contacts.found                     ▼
                      research.requested  → Researcher      (A02)
                      audit.requested     → Website Auditor (A03)
                      score.requested     → OpportunityScoring
                      match.requested     → Product Matcher (A04)
                      contacts.requested  → People Finder   (A05)
                      account.ready       → SDR (A06, ausente)
```

A ordem das etapas deixou de estar espalhada por cinco casos de uso, e uma conta
que chega pelo meio — importada já com pesquisa, ou reprocessada depois de um
sinal novo — retoma no ponto certo em vez de recomeçar.

`account.ready` é o único que ainda não tem consumidor: o SDR (A06) não existe. O
dispatcher o marca como processado e registra em log, em vez de entupir a fila em
silêncio.

**`account.created` não entra na cadeia automática.** Nenhum produtor o emite
hoje; quem passaria a emiti-lo é o pipeline de ingestão, que cria contas às
centenas de milhares. Ligá-lo ao Orchestrator faria uma carga nacional da Receita
pedir pesquisa para cada linha — decisão de orçamento, não de arquitetura. Entrar
no funil continua sendo ato explícito.

## Decisões que se afastam do blueprint

**`FOR UPDATE SKIP LOCKED` no claim do outbox.** O §20 descreve "claim event" sem
especificar mecanismo. Sem isso, dois workers pegam o mesmo evento e a pesquisa é
executada — e paga — duas vezes.

**A API rejeita pesquisa concorrente explicitamente.** A máquina de estados trata
`from == to` como no-op válido, o que é correto para reprocessar um evento. Mas no
caso de uso isso deixaria passar uma segunda pesquisa da mesma conta. A checagem
`status == researching` é separada e explícita.

**MCP somente leitura nesta entrega.** O §22 lista ferramentas de escrita
(`create_evidence`, `create_signal`). Elas quebrariam a atomicidade: a persistência
precisa ser uma transação, e um agente escrevendo incrementalmente não tem como
garantir isso. Entram quando houver fluxo interativo que as justifique.

**Dapper e não EF Core.** Ver [ADR-0003](adr/0003-dapper-em-vez-de-orm.md).

**Roteador de evento não é orquestrador.** O `OutboxDispatcher` roteia por
`event_type`; o Orchestrator do frame 05 da V2 *decide o próximo passo a partir do
estado da conta*. São coisas diferentes: o roteador é infraestrutura, o
orquestrador é política.

Isso está resolvido: `AccountOrchestration.Decide` é função pura no domínio e
`DecideNextActionUseCase` escreve o efeito dela. A separação entre **comando** e
**conclusão** é o que permitiu tirar a política do `switch` sem esvaziar o
dispatcher: rotear `audit.requested` para o auditor não depende do estado da
conta e continua onde estava.

O que saiu de lá foi a regra de que pesquisa concluída significa pontuar. Ela era
política escrita dentro de um adaptador — e, pior, o `switch` só enxergava o
evento que acabara de chegar, então não havia de onde perguntar "esta conta já
tem auditoria?". A cadeia era fixa por construção.

## Por que o custo é instrumentado desde já

"Custo de IA por conta pesquisada" é métrica auxiliar do §1 e o principal critério
para escalar de 1 → 10 → 30 → 100 contas (§34). `agent_runs` grava tokens, custo e
latência por execução, inclusive nas falhas — o custo foi incorrido de qualquer forma.

`GET /accounts/{id}/cost` responde direto.

O scoring, por outro lado, custa zero: é aritmética sobre fatos já persistidos.
Foi desenhado assim para poder rodar a cada sinal novo sem entrar nessa conta.

## Busca

Três endpoints sobre a migration `0011`:

```
GET /search/accounts?q=            full-text, ranqueado por peso de campo
GET /search/evidence?q=            full-text com trecho destacado e URL da fonte
GET /accounts/{id}/similar         trigrama, candidatos a merge de grupo econômico
```

`/accounts/{id}/similar` devolve a faixa de decisão do §11 (`auto` ≥ 0.90,
`provavel` ≥ 0.75, `revisao` abaixo) — mas **só ordena candidatos**. A decisão de
merge é do `AccountGroupResolver` (automática) ou da fila de revisão (humana).

Detalhes de configuração, pesos e a limitação do stemmer estão em
[data-model.md](data-model.md).
