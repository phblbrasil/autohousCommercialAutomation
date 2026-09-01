# AutoHous Revenue Engine

Motor de inteligência comercial para o mercado automotivo brasileiro.

**Entrega atual:** da base oficial da Receita Federal até uma conta agrupada,
pesquisada com evidência rastreável e priorizada por score explicável.

```
Dados Abertos CNPJ (RFB) → account graph → pesquisa com evidência → Opportunity Score
```

## Começando

```bash
cp .env.example .env
docker compose up -d db
export REVENUE_DB_CONNECTION="Host=localhost;Port=5433;Database=autohous_revenue;Username=revenue;Password=revenue"

dotnet run --project src/AutoHous.Revenue.Migrator     # aplica as migrations
./scripts/test.sh                                      # suíte completa
```

> Rode a suíte pelo `scripts/test.sh`, não pelo `dotnet test` cru. São 433
> testes, e o script existe por dois motivos.
>
> O primeiro: com o runner Microsoft.Testing.Platform eleito no `global.json`,
> `--nologo` faz o host localizar os módulos e não iniciar nenhum — "Zero testes
> executados", código 5. O script fixa a invocação correta.
>
> O segundo: um módulo cujo host não inicia **não aparece no sumário**. A
> contagem final o ignora inteiro e a suíte fica verde com uma bateria
> desligada. O script confere quem reportou e roda direto, pelo `dotnet`, quem
> faltou.

> Os testes de integração sobem um Postgres efêmero por classe via
> Testcontainers. Onde isso não roda com o Docker saudável do lado — o Smart App
> Control do Windows bloqueia o `Docker.DotNet.dll` no load —, preencha
> `REVENUE_TEST_DB_CONNECTION` no `.env` apontando para o servidor do compose:
> cada classe passa a criar e destruir um banco próprio nele.

## Captura de contas

### Da fonte oficial

Os Dados Abertos CNPJ da Receita Federal, direto do repositório dela — 7,3 GB no
release `2026-08`, ~63 milhões de estabelecimentos, dos quais o universo
automotivo entra na base e o resto vira agregado de mercado.

```bash
# competências publicadas (só consulta a origem, não toca no banco)
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita --list

# só o agregado de mercado: TAM por UF, CNAE e situação
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita --release 2026-08 --stats-only

# ensaio sobre as primeiras 200 mil linhas, sem gravar nada
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita \
  --release 2026-08 --dry-run --limit 200000

# carga nacional
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita --release 2026-08
```

```
Release 2026-08
  arquivos ................ 27 (6.428 MB)
  estabelecimentos lidos .. 63.412.887
  universo automotivo ..... 714.203
  empresas casadas ........ 612.940
```

O download é retomável e fica em cache: reexecutar não rebaixa o que já está
íntegro. `--offline` usa só o cache, para quem baixou por outro meio.

O quadro societário (`--socios`) é PII e opt-in — ver
[docs/governance.md](docs/governance.md).

### De um extrato delimitado

Para lista pequena ou fonte de terceiro. Reconhece as colunas por apelido, sem
depender da ordem, e aceita cabeçalho com acento:

```bash
# simula: roda a normalização real e não grava nada
dotnet run --project src/AutoHous.Revenue.Ingestor -- arquivo --file lista.csv --dry-run

# captura e resolve o grafo de contas
dotnet run --project src/AutoHous.Revenue.Ingestor -- arquivo \
  --file lista.csv --encoding latin1 --source "lista-parceiro"
```

```
Lote 01a01d82-555f-7d5f-bb0d-847023342cf2
  linhas lidas ............ 6
  gravadas ................ 6
  rejeitadas .............. 3
  contas criadas .......... 1
  CNPJs anexados .......... 2
  em revisao humana ....... 0
  resolucao automatica .... 50,0%
```

Fila de revisão do agrupamento:

```bash
# A API exige Authorization: Bearer em tudo, menos /health (ADR-0009).
# Esta função evita repetir o header em cada linha daqui para baixo.
set -a; . ./.env; set +a
api() { curl -H "Authorization: Bearer $REVENUE_API_KEY" "$@"; }

api localhost:5080/merge-candidates
api -X POST localhost:5080/merge-candidates/<ID>/decide \
  -H 'content-type: application/json' -d '{"approve":true,"decidedBy":"pedro"}'
```

## Slice de pesquisa e score

```bash
set -a; . ./.env; set +a          # REVENUE_API_KEY: sem ela a API não sobe
export AGENT_RUNTIME=fixture      # determinístico, sem custo, sem Hermes

dotnet run --project src/AutoHous.Revenue.Api &
dotnet run --project src/AutoHous.Revenue.Worker &

api -X POST localhost:5080/accounts \
  -H 'content-type: application/json' \
  -d '{"cnpj":"11222333000181","name":"Grupo Vento Sul","uf":"SP","municipio":"Bauru"}'

api -X POST localhost:5080/accounts/<ACCOUNT_ID>/research
api localhost:5080/accounts/<ACCOUNT_ID>/evidence
api localhost:5080/accounts/<ACCOUNT_ID>/score
api localhost:5080/accounts/<ACCOUNT_ID>/cost
```

A pesquisa emite `research.completed`, que o worker consome para calcular o
Opportunity Score e emitir `score.ready`. O score é determinístico e custa zero.

## Busca

Full-text em português (sem acento, com ranking e trecho destacado) e casamento
difuso de nomes por trigrama:

```bash
api "localhost:5080/search/evidence?q=unidades"         # acha "unidade" e "unidades"
api "localhost:5080/search/evidence?q=unidades -jornal" # exclui um termo
api "localhost:5080/search/accounts?q=vento sul"
api "localhost:5080/accounts/<ACCOUNT_ID>/similar?threshold=0.3"
```

O stemmer português dá stems diferentes para substantivo e verbo da mesma
família — `expansão` vira `expansa`, `expandindo` vira `expand` —, então
[SearchQueryExpander](src/AutoHous.Revenue.Domain/SearchQueryExpander.cs)
acrescenta os sinônimos do domínio antes da consulta virar `tsquery`: quem busca
`expansao` também alcança "o grupo está expandindo". A conta de exemplo não
exercita isso — a evidência de expansão dela fala em "inauguração" —, quem prova
é `FullTextSearchTests.Expansao_de_sinonimo_alcanca_a_forma_verbal`.

## A cadeia de agentes

Quatro dos seis agentes do §17 do blueprint existem. O que os encadeia não é uma
sequência fixa: é o Orchestrator, que lê o estado da conta e decide o próximo
passo.

```
conclusão de qualquer etapa
        ↓
  Orchestrator (A01)  ── lê v_account_progress, decide, emite UM comando
        ↓
  Researcher (A02)  →  Website Auditor (A03)  →  Opportunity Score
        ↓                                              ↓
  People Finder (A05)  ←  Product Matcher (A04)  ←  ────┘
        ↓
  account.ready   →   SDR (A06) — não existe ainda
```

Em três dos quatro, o modelo produz **fatos e texto**; a aritmética é da
plataforma. O Product Matcher leva isso ao extremo e inverte a ordem: a nota e a
porta de entrada são calculadas **antes** da chamada ao modelo, que recebe o
diagnóstico pronto e escreve o argumento. Ver
[ADR-0010](docs/adr/0010-plataforma-decide-agente-argumenta.md) e
[ADR-0011](docs/adr/0011-orchestrator-decide-por-estado.md).

O que falta — SDR, Reply Analyst, CRM Agent — depende de canal de saída, que não
existe.

## Documentação

| Documento | Conteúdo |
|---|---|
| [HERMES.md](HERMES.md) | Contexto do agente: o que ele pode e não pode fazer |
| [docs/gap-analysis.md](docs/gap-analysis.md) | O que está pronto, o que falta, contra os boards e o padrão de arquitetura |
| [docs/architecture.md](docs/architecture.md) | Camadas, fluxos e decisões |
| [docs/ingestion.md](docs/ingestion.md) | Captura, normalização e account graph |
| [docs/scoring.md](docs/scoring.md) | Opportunity Score e cobertura |
| [docs/data-model.md](docs/data-model.md) | Esquema, correções aplicadas, dívidas conscientes |
| [docs/agents.md](docs/agents.md) | Camada de agentes e pipeline de validação |
| [docs/governance.md](docs/governance.md) | Evidência, suppression, cooldown, idempotência, RLS |
| [docs/adr/](docs/adr/) | Decisões arquiteturais, com gatilho de revisão |

## Estrutura

```
database/migrations/   SQL puro, numerado, imutável
src/
  Domain           entidades e políticas puras — zero dependências
  Application      portas e casos de uso — nunca Npgsql nem HTTP
  Infrastructure   Npgsql + Dapper, implementa as portas
  Agents           IAgentRuntime, validador de schema, prompts versionados
  Api              Minimal API
  Worker           dispatcher do outbox
  Ingestor         CLI de captura
  Migrator         DbUp
  Mcp              MCP read-only, fala HTTP com a API
hermes/                schemas, prompts versionados e skills — um conjunto por agente
tests/                 domínio · aplicação · agentes · arquitetura · integração
```

As direções de dependência são impostas por
`tests/AutoHous.Revenue.Architecture.Tests` — não por convenção.

Backlog no Jira: projeto **ARI**.
