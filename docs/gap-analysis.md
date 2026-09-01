# Análise de lacunas — 20/08/2026

Confronto entre o que está construído e três documentos de referência:
`CLEAN_ARCHITECTURE_SKILL`, `AutoHous_Miro_Strategy_Board_Kit` (V1) e
`AutoHous_Miro_Strategy_Board_V2`.

---

## 1. O banco já existe. A captura não existia.

A dúvida do enunciado — *"precisamos montar a captura das infos e criar o banco;
não sei se isso já está contemplado"* — se resolve em duas respostas diferentes.

**Criar o banco: feito.** As migrations `0001`–`0011` cobrem as 15 tabelas P0 do
frame 03 da V2, com uma exceção e um extra:

| Tabela do frame 03 V2 | Migration | Situação |
|---|---|---|
| `accounts` | 0002 | ✅ |
| `companies_cnpj` | 0002 | ✅ |
| `account_locations` | 0002 / 0010 | ✅ |
| `account_brands` | 0002 | ✅ |
| `contacts` | 0003 | ✅ |
| `technologies` | — | ❌ ausente (P1, entra com o Website Auditor) |
| `signals` | 0004 | ✅ |
| `website_audits` | 0005 | ✅ |
| `product_fit` | 0005 | ✅ |
| `account_scores` | 0005 | ✅ |
| `touchpoints` | 0007 | ✅ |
| `conversations` | 0007 | ✅ |
| `opportunities` | 0007 | ✅ |
| `agent_runs` | 0006 | ✅ |
| `suppression_list` | 0008 | ✅ |

Extras já presentes que a V2 não pedia: `sources`/`evidence` separados (lastro da
Regra 1), `events_outbox` (frame 02 pede arquitetura de eventos e não define a
mecânica), `research_runs`, e busca full-text + trigrama (`0011`).

**Captura das infos: não existia.** Nenhuma das etapas 01–03 do frame 04 da V2
(*Seed → Normalize → Account Graph*) tinha código. O único caminho de entrada era
`POST /accounts` com um CNPJ por vez, digitado à mão. Sem isso, as 300 contas do
piloto da semana 1 seriam inseridas manualmente — e o `account_graph`, que é o
princípio de desenho nº 1 da V2 (*"Account > CNPJ"*), não existia em lugar nenhum.

Isso é o que a entrega desta noite constrói. Ver seção 4.

---

## 2. Lacunas contra o padrão de Arquitetura Limpa

O padrão do §13 da skill exige, para solução .NET de médio porte:

```
Api ----------> Application -------> Domain
Worker -------> Application -------> Domain
Infrastructure -> Application ------> Domain
```

O que existia:

```
Api ----------> Infrastructure -----> Domain
Worker -------> Infrastructure -----> Domain
```

### 2.1 Não havia camada de aplicação

**Sintoma.** `RevenueEndpoints.cs` continha a política de negócio inteira do caso
de uso "solicitar pesquisa": checagem de suppression (Regra 2), cooldown mensal
(Regra 3), rejeição de run concorrente, validação de transição de estado, e a
composição transacional `research_run + status + outbox`. São ~90 linhas de regra
dentro de um lambda de endpoint HTTP.

**Violações.** §5.2 (casos de uso são camada própria), §12 (controllers devem
apenas traduzir), §24 (*"regra de negócio em controller ou endpoint"* está na
lista de antipadrões proibidos), §17.2 (a regra só era testável subindo
`WebApplicationFactory` + Postgres real).

### 2.2 Portas declaradas do lado da implementação

`IAccountRepository`, `IOutboxRepository`, `IResearchRunRepository`,
`IAgentRunRepository`, `ISearchRepository`, `IResearchProfilePersister`,
`IUnitOfWork` — todos declarados **dentro** de `AutoHous.Revenue.Infrastructure`.

**Violação do §8.5 (DIP):** *"abstrações pertencem ao lado que precisa da
capacidade, não ao fornecedor da implementação"*. Na prática: qualquer consumidor
da porta precisava referenciar o assembly do Npgsql para enxergá-la.

### 2.3 Tipo de infraestrutura atravessando a fronteira

```csharp
public interface IUnitOfWork : IAsyncDisposable
{
    NpgsqlConnection Connection { get; }   // ← detalhe de fornecedor no contrato
    NpgsqlTransaction Transaction { get; }
}
```

**Violação do §6.1:** objetos de SDK não podem circular por contratos internos.
Enquanto `IUnitOfWork` expuser `NpgsqlConnection`, trocar Postgres por qualquer
outra coisa — ou testar um caso de uso sem banco — é impossível por construção.

### 2.4 SQL no adaptador de entrada

`GET /accounts/{id}/evidence` monta e executa Dapper direto no endpoint.
§11 (*"SQL fica na infraestrutura"*) e §24 (*"controllers não acessam banco
diretamente"*).

### 2.5 Nenhuma verificação automática de fronteira

§29 lista invariantes que o pipeline deve impor — `Domain não importa
Infrastructure`, `nenhum ciclo`, etc. A arquitetura estava descrita em
`HERMES.md` e em nada mais. §24 chama isso pelo nome: *"arquitetura descrita em
documento, mas não imposta pelo código"*.

A única fronteira realmente imposta era a do MCP, que não referencia Npgsql — e
o próprio `HERMES.md` a descreve com orgulho justificado: *"a fronteira é
estrutural, verificável no output de build"*. Era a exceção, não a regra.

### 2.6 Sem ADRs

§28 exige ADR para escolha de persistência, mensageria, estratégia de
consistência e quebras intencionais de regra. As decisões existem e estão bem
justificadas em `docs/architecture.md` — mas em prosa, sem contexto/opções/
consequências/gatilho de revisão.

### 2.7 O que já estava certo

Vale registrar, porque é o que tornou o refactor barato:

- **Domínio puro de verdade.** O `.csproj` do Domain tem um comentário dizendo
  que é livre de I/O — e é: zero `PackageReference`.
- **Fronteira do MCP imposta pelo grafo de dependências**, não por convenção.
- **Outbox transacional** com `SKIP LOCKED`, backoff e dead-letter — exatamente o
  que o §16 recomenda para consistência entre componentes.
- **Anticorrupção no Hermes** (§15): `HermesAgentRuntime` tolera variações de
  envelope e normaliza o erro; `StructuredOutputValidator` + `EvidenceFirstGuard`
  impedem que payload de fornecedor entre cru no domínio.
- **Migrations SQL puras e imutáveis**, com o schema como fonte única.

---

## 3. Lacunas contra a Fase 1 dos boards

O piloto da V2 (frame 11) define a semana 1 como *"fundação de dados: CNPJ →
accounts → grupos, 300 contas seed, ≥90% válidas"*. O backlog V2 (frame 13) lista
D01, D02 e D03 como P0 da W1.

| ID | Tarefa | Antes | Depois desta entrega |
|---|---|---|---|
| D01 | Schema Supabase core | ✅ migrations 0001–0011 | ✅ + `0012` |
| D02 | Pipeline CNPJ/CNAE — 300 contas seed | ❌ inexistente | ✅ ver seções 4 e 6 |
| D03 | Resolver grupo econômico + confidence + review queue | ❌ inexistente | ✅ ver seção 4 |
| A01 | Hermes Orchestrator (event → next_action) | 🟡 `OutboxDispatcher` roteia, mas não decide | ✅ fechado depois (ver seção 6) |
| A02 | Researcher com evidence schema | ✅ entregue | ✅ |
| A03 | Website Auditor | ❌ | ✅ entregue depois (ver seção 6) |
| A04 | Product Matcher + scoring | ❌ | ✅ fechado depois (ver seção 6) |
| A05 | People Finder | ❌ | ✅ fechado depois (ver seção 6) |
| A06 | SDR draft agent | ❌ | ❌ |
| R01 | Cadência + state machine | 🟡 state machine de account existe | 🟡 |
| G01 | Suppression + cooldown | ✅ | ✅ |

Duas observações sobre o gap A01:

- O `OutboxDispatcher` **roteia** eventos por tipo, mas o Orchestrator da V2
  *decide o próximo passo a partir do estado da conta*. São coisas diferentes. O
  roteador é infraestrutura; o orquestrador é política. Quando o segundo existir,
  ele é um caso de uso, não um `switch` no dispatcher.
- `research.completed` estava sendo marcado como processado sem consumidor. Com o
  scoring desta entrega, ele passa a ter um.

---

## 4. O que esta entrega construiu

Ver [ingestion.md](ingestion.md) para o pipeline de captura e
[scoring.md](scoring.md) para o motor de score. Resumo:

**Alinhamento arquitetural**
- `AutoHous.Revenue.Application`: portas + casos de uso. Api e Worker passam a
  depender dela, não da Infrastructure.
- `IUnitOfWork` sem Npgsql no contrato.
- Casos de uso extraídos do endpoint e do handler.
- `AutoHous.Revenue.Architecture.Tests`: as invariantes do §29 impostas por teste.
- `AutoHous.Revenue.Application.Tests`: regra de negócio testada com portas
  falsas, sem banco.
- ADRs em `docs/adr/`.

**Captura das infos (D02 + D03)**
- Migration `0012`: `ingestion_batches`, `companies_raw` (lineage), 
  `account_merge_candidates` (review queue).
- `CnaeCatalog` e `SegmentClassifier` no domínio: quais CNAEs são universo
  AutoHous e que tipo de operação cada um representa.
- `CompanyNormalizer`: razão social, município/UF, CNPJ com dígito verificador.
- `AccountGroupResolver`: raiz de CNPJ, nome normalizado e trigrama → decisão
  `auto` / `revisao`, com confidence.
- `AutoHous.Revenue.Ingestor`: CLI que lê CSV da Receita e roda o pipeline.
- Endpoints de lote e de fila de revisão.

**Scoring (frame 06)**
- `OpportunityScoring` no domínio: 30/30/25/15 com breakdown explicável e
  decaimento por recência de sinal.
- Consumidor de `research.completed` → grava `account_scores` → emite
  `score.ready`.

---

## 5. Camada 01 — a fonte primária (20/08/2026, à noite)

A seção 4 fechou D02 lendo um **extrato delimitado**: um CSV com cabeçalho, com
todos os campos numa linha, produzido por alguém fora do sistema. Isso resolvia a
mecânica e deixava a origem como pré-requisito manual.

A camada 01 troca a origem pela fonte oficial — os **Dados Abertos CNPJ da
Receita Federal**, direto do repositório dela. Ver
[ingestion.md](ingestion.md#a-fonte-oficial-dados-abertos-cnpj).

**O que mudou de fato**

| Antes | Agora |
|---|---|
| alguém achata a base em outra ferramenta | `receita --release 2026-08` baixa, lê e carrega |
| CSV com cabeçalho, campos por apelido | layout posicional oficial, sem cabeçalho, ISO-8859-1 |
| município como texto do extrato | join com `Municipios.zip` — o arquivo traz código próprio da RF |
| razão social e porte, se o extrato trouxesse | junção de `Estabelecimentos` + `Empresas` + `Simples` |
| nada sobre o mercado fora do recorte | `rf_cnae_stats` conta os ~63M por UF, CNAE e situação |
| `natureza_juridica`, `porte`, `data_abertura`, `cnaes_secundarios` vazios desde a 0002 | preenchidos |
| `account_locations` só via pesquisa | uma loja por estabelecimento, de graça |
| lineage do arquivo local | release + SHA-256 de cada zip em `receita_releases` |

**Peças novas**

- `AutoHous.Revenue.ReceitaFederal` — adaptador da fonte: WebDAV, retomada por
  `Range`, cache, zip, ISO-8859-1, layout posicional. Não conhece persistência, e
  `DependencyRuleTests` impõe isso.
- `PrepareReceitaReleaseUseCase` — as quatro passadas, o filtro de origem, o
  agregado e a junção. Para na linha pronta: quem captura e quem agrupa continuam
  sendo os mesmos de antes.
- `IngestCompanyStreamUseCase` — captura em blocos, uma transação por bloco.
  700 mil linhas em transação única segurariam locks por minutos.
- `MarketStatisticsAccumulator` (domínio) + `rf_cnae_stats` / `rf_municipio_stats`.
- Migration `0013` — release, agregado, colunas novas em `companies_cnpj` e o
  índice `left(cnpj, 8)` que faltava.
- Migration `0014` — `company_partners`, isolada por ser PII.

**Um defeito de escala corrigido de passagem.** `FindCandidatesAsync` filtra por
`left(c.cnpj, 8)` e não havia índice sobre essa expressão. Com dezenas de CNPJs,
irrelevante; com as ~700 mil linhas de uma carga nacional, um seq scan **por
linha do lote** sobre uma tabela que cresce durante a própria carga.

**Duas decisões que exigiram ADR**

- [ADR-0007](adr/0007-filtro-de-cnae-na-origem.md) — filtrar CNAE na origem sem
  quebrar o princípio da linha crua do ADR-0004.
- [ADR-0008](adr/0008-socios-e-pii.md) — sócios em tabela e migration próprias,
  atrás de opt-in.

**O que a camada 01 não faz**

- Não cria `contacts`. Telefone e e-mail da RFB são contato da PJ e ficam em
  `companies_cnpj`; contato de pessoa é o People Finder (A05).
- Não decide `tier` nem `store_count` — `account_locations` passa a ter o insumo,
  o cálculo não existe.
- Não resolve grupo econômico por sócio comum. `company_partners` é o insumo;
  a regra continua sendo raiz de CNPJ e nome.

---

## 6. A camada de agentes, fechada — 31/08/2026

A seção 6 desta análise listava sete pendências. Cinco fecharam em duas
entregas; duas continuam abertas, e por um motivo que não é técnico.

### Fechado pela entrega do Website Auditor

| # | Pendência | Como fechou |
|---|---|---|
| 1 | Website Auditor (A03) | sonda determinística + agente; `AutoHous.Revenue.WebAudit` |
| 6 | `technologies` ausente | migration `0015`, com `source` distinguindo medição de inferência |

A `0015` também trocou `website_audits.evidence_ids uuid[]` pela tabela de
ligação que a `0005` havia registrado como dívida.

### Fechado por esta entrega

| # | Pendência | Como fechou |
|---|---|---|
| 2 | Product Matcher (A04) | `ProductFitScoring` (determinístico, ADR-0005) + agente que escreve o argumento |
| 3 | People Finder (A05) | contrato com PII, `PersonaCatalog`, `ContactPolicy`, migration `0017` |
| 7 | Orchestrator de verdade (A01) | `AccountOrchestration.Decide` no domínio + `DecideNextActionUseCase` |

**Quatro dos seis agentes do §17 existem**, cada um com o conjunto completo:
prompt versionado, JSON Schema, skill do Hermes, porta de runtime, validador com
ciclo de reparo, guard de evidência, persistência transacional e cinco fixtures.

Três notas sobre o que foi decidido pelo caminho:

- **O Product Matcher inverte a ordem dos outros agentes.** A plataforma calcula
  o fit primeiro; o agente recebe o diagnóstico pronto e escreve o argumento.
  Duas consequências: ele não consegue escolher o produto errado, porque não
  escolhe, e a aritmética sobrevive à falha dele — a fila continua priorizada,
  falta só a frase. É o único dos quatro cuja falha não perde a etapa.
- **O People Finder tem duas camadas de guarda em vez de uma.** Além da Regra 1,
  vale o `ContactPolicy`: piso de confiança por contato e por canal, e a regra de
  que **cada canal aponta para evidência diferente da do contato**. Sem ela, um
  e-mail deduzido de `nome.sobrenome@` passa em qualquer schema, tem formato
  válido e aponta para uma fonte real — a notícia que citava o nome.
- **`EntryMinimumCoverage` não decide nada com os pesos de hoje.** Está
  documentado no código e há teste que falha se um rebalanceamento o tornar
  ativo. Um guard inativo que ninguém sabe estar inativo é pior que guard nenhum.

### O que continua faltando

1. **SDR + approval flow (A06, G01 parcial)** — Regra 4
   (`requires_human_approval`) está documentada e não implementada.
2. **Reply Analyst e CRM Agent** — dependem do mesmo pré-requisito.

Os três dependem de **canal de saída** — e-mail ou WhatsApp —, que é a maior peça
de infraestrutura nova desde o auditor, e de uma decisão de governança sobre
aprovação humana que não é de engenharia. Até lá, `account.ready` fica pendente
na fila com registro explícito de que a conta está pronta e não há quem a aborde.

Uma pendência menor, registrada para não virar surpresa: **`account.created` não
entra na cadeia automática**. Ver [architecture.md](architecture.md#a-cadeia-completa-depois-do-orchestrator).
