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

> `dotnet test` roda os 416 testes — mas **nunca com `--nologo`**. Com o runner
> Microsoft.Testing.Platform eleito no `global.json`, essa flag faz o host
> localizar os módulos e não iniciar nenhum: "Zero testes executados", código 5.
> `scripts/test.sh` fixa a invocação correta.

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
api "localhost:5080/search/evidence?q=expansao"        # acha "expandindo" via sinônimos
api "localhost:5080/search/evidence?q=unidades -jornal"
api "localhost:5080/search/accounts?q=vento sul"
api "localhost:5080/accounts/<ACCOUNT_ID>/similar?threshold=0.3"
```

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
hermes/                schema, prompt versionado, skill, config de exemplo
tests/                 domínio · aplicação · agentes · arquitetura · integração
```

As direções de dependência são impostas por
`tests/AutoHous.Revenue.Architecture.Tests` — não por convenção.

Backlog no Jira: projeto **ARI**.
