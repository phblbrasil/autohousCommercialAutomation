# HERMES.md — contexto do AutoHous Revenue Engine

Este arquivo é lido pelo agente. Define o que ele precisa saber e, principalmente,
o que ele **não** pode fazer.

> **Nenhum segredo neste arquivo.** Chaves vivem em variáveis de ambiente. Ver `.env.example`.

---

## O que é este sistema

Motor de inteligência comercial para o mercado automotivo brasileiro. Ele descobre
contas (concessionárias, grupos, revendas), pesquisa cada uma, registra evidências
rastreáveis, e — em etapas futuras — recomenda produto, encontra decisores e gera
abordagem para aprovação humana.

**Entrega atual:** da captura de uma base de CNPJs até uma conta agrupada,
pesquisada com evidência rastreável e priorizada por Opportunity Score.

## Regra central

```
LLM sugere; plataforma valida.
```

O agente produz um Research Profile em JSON. A plataforma valida contra schema,
verifica o lastro de evidências e só então grava — em uma transação. Saída inválida
nunca vira escrita parcial.

---

## O que o agente PODE fazer

- Pesquisar a conta em fontes públicas (site institucional, imprensa, registros).
- Extrair operação, marcas, unidades, estoque estimado e sinais.
- Registrar cada afirmação como evidência com URL e data de observação.
- Autoavaliar a completude da pesquisa.
- Ler contexto pelo MCP: `get_account_context`, `list_account_evidence`, `get_product_catalog`.

## O que o agente NÃO PODE fazer

- **Escrever no banco.** Não existe ferramenta de escrita. A persistência é da plataforma.
- **Escrever mensagem comercial** durante a pesquisa. Isso é do agente SDR, em outra etapa.
- **Afirmar sem fonte.** Toda marca, unidade e sinal precisa de `evidence_index` válido.
- **Alterar estado de conta.** Transição de status passa por `AccountStatusTransitions`.
- **Enviar qualquer coisa** por e-mail, WhatsApp ou LinkedIn.
- **Decidir supressão ou cooldown.** São regras determinísticas da plataforma.
- **Calcular ou influenciar o score.** O Opportunity Score é aritmética sobre
  fatos já persistidos. O agente produz os fatos; a plataforma faz a conta.
- **Decidir merge de grupo econômico.** Acima de 0.90 de similaridade com a mesma
  UF, ou com raiz de CNPJ igual, a plataforma une sozinha. Na faixa cinzenta,
  quem decide é uma pessoa.

---

## Contratos

| Artefato | Caminho |
|---|---|
| Schema do Research Profile | `hermes/schemas/research-profile.schema.json` |
| Prompt versionado | `hermes/prompts/researcher.v1.md` |
| Skill | `hermes/skills/autohous-account-research/SKILL.md` |
| Config de exemplo | `hermes/config/config.example.yaml` |
| Setup local do agente | `scripts/hermes-setup.sh` |
| Respostas gravadas | `tests/fixtures/agent-runs/researcher/` |

O schema é a autoridade. Mudou o contrato? Nova versão de prompt, nunca edição da anterior —
`agent_runs.prompt_version` é o que permite comparar safras.

---

## Como o Hermes é acionado

O Revenue Worker chama o **API Server** do Hermes (`POST /v1/runs`, Bearer com
`HERMES_API_SERVER_KEY`), acompanha o run e extrai o texto final.

Dois fatos da documentação oficial que moldam a arquitetura:

1. **O Hermes não endereça agentes por nome.** `POST /v1/runs` não recebe "qual agente
   executar" e `delegate_task` é decidido pelo modelo em runtime. Os seis agentes do
   blueprint são conceitos **nossos**: o worker escolhe prompt + skill + schema por
   tipo de run. `agent_runs.agent_name` é rótulo da aplicação.
2. **Skills não têm structured output forçado.** Não existe garantia de JSON válido.
   Por isso o validador com ciclo de reparo é caminho crítico, não hardening opcional.

---

## Arquitetura

```
Dados Abertos CNPJ (RFB)  ── Ingestor `receita`   ── rf_cnae_stats (os ~63M)
arquivo delimitado        ── Ingestor `arquivo`
        ↓  companies_raw (linha crua, sem interpretação)
CompanyNormalizer + AccountGroupResolver
        ↓  conta nova | CNPJ anexado | fila de revisão humana

POST /accounts/{id}/research
        ↓  (uma transação)
research_runs + accounts.status + events_outbox
        ↓
Revenue Worker  ── claim com FOR UPDATE SKIP LOCKED
        ↓
IAgentRuntime  ── hermes | fixture
        ↓
StructuredOutputValidator + EvidenceFirstGuard  ── 1 tentativa de reparo
        ↓  (uma transação)
sources → evidence → signals → brands → locations → accounts
        → research_runs → agent_runs → research.completed
        ↓
ScoreAccountUseCase  ── determinístico, sem agente, custo zero
        ↓  (uma transação)
account_scores + accounts.tier + accounts.status → score.ready
```

Toda **conclusão** volta para o Orchestrator (A01), que lê o estado da conta e
emite o **comando** seguinte — auditar, pontuar, casar produto, buscar contato ou
marcar pronta. O worker roteia comando por tipo, que é infraestrutura; a decisão
do que vem depois é política e vive em `AccountOrchestration.Decide`, função pura
no domínio. Ver
[ADR-0011](docs/adr/0011-orchestrator-decide-por-estado.md).

A API **nunca** chama o Hermes de forma síncrona.

| Projeto | Responsabilidade |
|---|---|
| `AutoHous.Revenue.Domain` | Entidades e regras puras. Sem I/O, sem dependências. |
| `AutoHous.Revenue.Application` | Portas e casos de uso. Nunca Npgsql nem HTTP. |
| `AutoHous.Revenue.Infrastructure` | Npgsql + Dapper. Implementa as portas. |
| `AutoHous.Revenue.Agents` | `IAgentRuntime`, cliente Hermes, fixture, validador, prompts. |
| `AutoHous.Revenue.Api` | Minimal API. |
| `AutoHous.Revenue.Worker` | Dispatcher do outbox. |
| `AutoHous.Revenue.Ingestor` | CLI de captura: `receita` (fonte oficial) e `arquivo` (extrato). |
| `AutoHous.Revenue.ReceitaFederal` | Fonte oficial de CNPJ: WebDAV, zip, ISO-8859-1, layout posicional. |
| `AutoHous.Revenue.Migrator` | DbUp sobre SQL puro. |
| `AutoHous.Revenue.WebAudit` | Sonda de site do A03: HTTP e HTML. Não referencia Npgsql. |
| `AutoHous.Revenue.Mcp` | MCP read-only. Não referencia Npgsql. |

As direções de dependência são impostas por `AutoHous.Revenue.Architecture.Tests`,
não por convenção.

---

## Comandos

```bash
docker compose up -d db                                    # Postgres 17 em :5433
dotnet run --project src/AutoHous.Revenue.Migrator         # aplica migrations
./scripts/test.sh                                          # suíte completa
dotnet run --project src/AutoHous.Revenue.Api              # API
dotnet run --project src/AutoHous.Revenue.Worker           # worker
```

`AGENT_RUNTIME=fixture` (padrão) roda determinístico, sem custo e sem Hermes.
`AGENT_RUNTIME=hermes` exige `hermes gateway` no ar e credencial de provider de modelo.

```bash
./scripts/hermes-setup.sh                                  # MCP + config.yaml + chave
hermes setup --portal                                      # credencial (interativo)
hermes gateway                                             # sobe o API Server em :8642
hermes mcp test autohous_revenue                           # 3 ferramentas, allowlist
```

`scripts/hermes-setup.sh` é idempotente: publica o MCP, gera `API_SERVER_KEY`, aponta
`skills.external_dirs` para `hermes/skills/` deste repositório — a skill versionada é a
que roda, sem cópia para envelhecer — e espelha a chave no `.env`.

---

## Segurança

- O agente **não recebe** `service_role` nem string de conexão.
- O MCP fala com a Revenue API por HTTP; o projeto não referencia Npgsql — a fronteira
  é estrutural, verificável no output de build.
- **A Revenue API exige `Authorization: Bearer`** ([ADR-0009](docs/adr/0009-credencial-de-borda-da-revenue-api.md)).
  Sem `REVENUE_API_KEY` (ou `REVENUE_API_KEY_FILE`) utilizável, a API **não sobe** —
  subir aberta seria pior, porque responderia 200 parecendo saudável. `/health` fica
  aberto para probe de liveness; todo o resto exige a chave.
- O segredo da API vive em **arquivo `0600`** (`~/.hermes/secrets/revenue-api-key`), e o
  `config.yaml` guarda só o caminho. Dois motivos: o Hermes não interpola `${VAR}` no
  bloco `env:` do MCP, e variável de ambiente vaza em `docker inspect` e em
  `/proc/{pid}/environ`. É o mesmo formato que Docker secret e Kubernetes montam.
- A allowlist de ferramentas do Hermes é **por servidor**, não por agente: `tools.include`
  no `config.yaml` é a única fronteira efetiva.
- O API Server do Hermes expõe a superfície completa de ferramentas, incluindo execução
  de terminal. Manter em `127.0.0.1`, CORS desabilitado.
- RLS habilitada em todas as tabelas desde a migration `0009`.

**Para HML/PRD, o que ainda não está resolvido:** a chave viaja em claro — Bearer sobre
HTTP só é aceitável no laço local, então a API precisa de TLS terminado à frente. E não
há autorização por consumidor: quem tem a chave alcança toda a superfície, inclusive a
de escrita. O gatilho de revisão está no ADR-0009.
