# Mapa — por onde navegar

Ponto de entrada único do repositório. Não explica nada em profundidade: diz
**onde** está a explicação, e o que você precisa saber para chegar lá sem se
perder.

Se você só tem cinco minutos, leia o [HANDOFF.md](../HANDOFF.md). Ele responde
"onde paramos". Este arquivo responde "onde fica".

---

## 1. Quero… → vá para

| Quero | Vá para |
|---|---|
| saber o estado atual e o próximo passo | [HANDOFF.md](../HANDOFF.md) |
| entender a regra que governa tudo | [CLAUDE.md](../CLAUDE.md) § *Regra central do produto* |
| entender o que o agente pode e não pode | [HERMES.md](../HERMES.md) |
| rodar o Hermes ponta a ponta | [docs/hermes-runbook.md](hermes-runbook.md) |
| entender a arquitetura em camadas | [docs/architecture.md](architecture.md) |
| entender a camada de agentes e a validação | [docs/agents.md](agents.md) |
| entender as tabelas | [docs/data-model.md](data-model.md) |
| entender o Opportunity Score | [docs/scoring.md](scoring.md) |
| entender a carga da Receita | [docs/ingestion.md](ingestion.md) |
| saber por que a carga demorava 12 h | [docs/carga-receita-otimizacao.md](carga-receita-otimizacao.md) |
| saber o que ainda falta do blueprint | [docs/gap-analysis.md](gap-analysis.md) |
| entender PII, suppression e LGPD | [docs/governance.md](governance.md) |
| subir em container | [docs/deploy-railway.md](deploy-railway.md) |
| saber **por que** decidimos algo | [docs/adr/](adr/) |

---

## 2. O funil, e onde cada pedaço mora

A cadeia é dirigida por eventos. Cada **conclusão** volta para o Orchestrator,
que lê o estado inteiro da conta e decide o próximo **comando**.

```
research → score → match → contacts → ready
```

| | Agente | Decide o quê | Domínio (a aritmética) | Persiste em |
|---|---|---|---|---|
| **A01** | Orchestrator | o próximo passo | [`AccountOrchestration`](../src/AutoHous.Revenue.Domain/AccountOrchestration.cs) | — |
| **A02** | Researcher | o retrato da empresa | — *(único cujo output é todo do agente)* | `ResearchProfilePersister` |
| **A03** | Website Auditor | o que a página significa | [`WebsiteAuditScoring`](../src/AutoHous.Revenue.Domain/WebsiteAuditScoring.cs) | `WebsiteAuditPersister` |
| **A04** | Product Matcher | o argumento e a objeção | [`ProductFitScoring`](../src/AutoHous.Revenue.Domain/ProductFitScoring.cs) | `ProductFitPersister` |
| **A05** | People Finder | quem decide e por onde falar | [`PersonaCatalog`](../src/AutoHous.Revenue.Domain/PersonaCatalog.cs) | `ContactPersister` |
| **A06** | SDR | — | — | **não existe** |

O roteamento fica em [`OutboxDispatcher`](../src/AutoHous.Revenue.Worker/OutboxDispatcher.cs);
a decisão, em [`DecideNextActionUseCase`](../src/AutoHous.Revenue.Application/UseCases/Orchestration/DecideNextActionUseCase.cs).
A distinção é o [ADR-0011](adr/0011-orchestrator-decide-por-estado.md): rotear
comando por tipo é infraestrutura, decidir o próximo passo é política.

**A regra que explica o desenho todo:** o LLM sugere, a plataforma valida. Toda
nota é aritmética nossa; o agente produz fato com evidência rastreável. Ver
[ADR-0005](adr/0005-scoring-deterministico.md) e
[ADR-0010](adr/0010-plataforma-decide-agente-argumenta.md).

---

## 3. Os comandos que você vai repetir

Tudo no WSL — não é preferência, é o que funciona nesta máquina
([por quê](hermes-runbook.md#por-que-wsl)).

```bash
wsl -d Ubuntu-24.04
cd /mnt/d/projects/autohousCommercialAutomation
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
set -a; . ./.env; set +a
```

| | |
|---|---|
| suíte completa | `./scripts/test.sh` — **nunca** `dotnet test --nologo` |
| aplicar migrations | `dotnet run --project src/AutoHous.Revenue.Migrator -- "$REVENUE_DB_CONNECTION"` |
| API (porta 5080) | `dotnet run --project src/AutoHous.Revenue.Api` |
| worker | `dotnet run --project src/AutoHous.Revenue.Worker` |
| gateway do Hermes (8642) | `hermes gateway` |
| diagnóstico do Hermes | `hermes doctor` |
| ferramentas do MCP | **não** use `hermes mcp test` — está quebrado. Ver [HANDOFF §6](../HANDOFF.md) |

**A ordem importa:** a API sobe antes do gateway, porque o MCP aponta para
`127.0.0.1:5080`. E a API precisa estar **no WSL** — uma API no Windows não é
alcançável desse endereço.

---

## 4. As armadilhas que já custaram tempo

Cada uma destas foi descoberta na prática, custou horas, e está documentada onde
o link aponta. Ler antes de escrever consulta nova economiza a redescoberta.

| Armadilha | Sintoma | Onde |
|---|---|---|
| **Cast implícito em `character(n)`** | seq scan; 50,7 ms contra 0,076 ms | [CLAUDE.md](../CLAUDE.md), [otimização](carga-receita-otimizacao.md) |
| **FK sem índice do lado que referencia** | `DELETE` de 839k rodou 10 min | migration `0016` |
| **Limiar do `%` do `pg_trgm`** | índice arrasta candidato que a query descarta | [CLAUDE.md](../CLAUDE.md) |
| **`DateTimeOffset` com offset ≠ 0** | falha só no `INSERT`, passa por schema e guard | `Infrastructure/Timestamps.cs` |
| **Transporte do Hermes** | todo run como `contract_violation` | [runbook](hermes-runbook.md), [HANDOFF §4.2](../HANDOFF.md) |
| **`agent_run` gravado depois da linha que o referencia** | `23503` dentro da transação | [HANDOFF §5](../HANDOFF.md) |
| **`--nologo` no `dotnet test`** | "Zero tests ran", código 5 | [CLAUDE.md](../CLAUDE.md) |
| **`.env` com CRLF** | `400` com corpo vazio em toda chamada | [runbook](hermes-runbook.md) |
| **Smart App Control** | `An Application Control policy has blocked this file` | [HANDOFF §3](../HANDOFF.md) |

O padrão que se repete: **defeito invisível na escala ou no ambiente em que o
código foi escrito, dominante naquele em que ele roda.** Fixture esconde fuso;
mil linhas escondem cast; teste sem banco esconde ordem de escrita.

---

## 5. Como trabalhamos

- **Branch por fatia.** Nada vai direto na `main`. Cada unidade de trabalho abre
  sua branch (`feat/`, `fix/`, `docs/`, `test/`), fecha com commit pequeno e é
  mergeada assim que está verde. Não acumular.
- **`./scripts/test.sh` verde antes do merge.** Sempre.
- **O HANDOFF acompanha o merge.** Um mapa que não descreve o que está na `main`
  é pior que mapa nenhum.
- **Decisão com contexto vira ADR**, não comentário perdido. O gatilho de revisão
  faz parte do ADR: sem ele, ninguém sabe quando reabrir.

---

## 6. Onde ficam as coisas

```
src/
  AutoHous.Revenue.Domain/          aritmética e regra pura — sem banco, sem HTTP
  AutoHous.Revenue.Application/     casos de uso e portas (interfaces)
  AutoHous.Revenue.Infrastructure/  Dapper, Npgsql, persisters
  AutoHous.Revenue.Agents/          runtime de agente, prompts, validação de schema
  AutoHous.Revenue.WebAudit/        sonda HTTP do A03
  AutoHous.Revenue.Api/             endpoints REST (Bearer em tudo menos /health)
  AutoHous.Revenue.Worker/          OutboxDispatcher
  AutoHous.Revenue.Ingestor/        carga da Receita Federal
  AutoHous.Revenue.Mcp/             servidor MCP — 3 ferramentas de leitura
  AutoHous.Revenue.Migrator/        DbUp

database/migrations/                a fonte da verdade do schema
hermes/                             prompts versionados, schemas JSON, skills
tests/fixtures/agent-runs/          respostas gravadas por agente e cenário
scripts/                            test.sh, hermes-setup.sh
```

A dependência é de fora para dentro: `Domain` não conhece ninguém, e
[testes de arquitetura](../tests/AutoHous.Revenue.Architecture.Tests/DependencyRuleTests.cs)
impõem isso mecanicamente.

---

## 7. Diário — o que fizemos, em ordem

O que cada fatia mudou e onde está registrada. Serve para responder "quando isso
entrou, e por quê?" sem `git log`.

| Data | Fatia | O que mudou | Onde ler |
|---|---|---|---|
| 31/08 | Fundação | camadas, outbox, scoring, Researcher (A02) | ADR-0001 a 0008 |
| 31/08 | Credencial de borda + ICP | Bearer na API, camadas de ICP, envelope do Hermes | [ADR-0009](adr/0009-credencial-de-borda-da-revenue-api.md) |
| 31/08–01/09 | Website Auditor (A03) + carga nacional | migration `0015`/`0016`, sonda HTTP, 3 defeitos de escala na carga | [HANDOFF §4](../HANDOFF.md), [otimização](carga-receita-otimizacao.md) |
| 01/09 | Revisão de A01, A04, A05 | 6 defeitos corrigidos: laço de pesquisa, safra instável, escala do AutoFollow, run órfão, desqualificador eterno, fit abaixo do corte | [HANDOFF §5](../HANDOFF.md) |
| 01/09 | Migration `0017` aplicada | `v_account_progress`, `product_fit_evidence`, `contact_evidence`, pisos de confiança | [HANDOFF §1](../HANDOFF.md) |
| 01/09 | Hermes verificado | credencial, gateway, MCP e allowlist conferidos; `hermes mcp test` quebrado | [HANDOFF §6](../HANDOFF.md) |
| 01/09 | Ensaio em fixture | cadeia completa até `ready`; achou a FK de `agent_run_id` | [HANDOFF §5](../HANDOFF.md) |
| 02/09 | Cobertura de integração de A04 e A05 | `ProductFitSliceTests` — os dois persisters que não tinham banco real nos testes agora têm | [ProductFitSliceTests](../tests/AutoHous.Revenue.Integration.Tests/ProductFitSliceTests.cs) |
| 02/09 | AutoTalk fora da oferta | não está pronto para vender e estava ganhando a porta de entrada; continua pontuado, não abre conversa | [ProductCatalog](../src/AutoHous.Revenue.Domain/ProductCatalog.cs) |
| 02/09 | Fit por perfil de operação | ICP decidido (venda de veículos, 38.332 contas; oficina/autopeças fora por ora), faixas por unidades × porte, 8 hipóteses de dor | [ADR-0012](adr/0012-fit-por-perfil-de-operacao.md) |
| 02/09 | Sonda de site coberta | projeto de teste que não existia; fixa o comportamento atual **e dois defeitos do regex**, antes de trocar o motor de parse | [HttpWebsiteProbeTests](../tests/AutoHous.Revenue.WebAudit.Tests/HttpWebsiteProbeTests.cs) |
| 02/09 | Parser de HTML real | regex → AngleSharp na sonda; os dois defeitos fixados quebraram e viraram correção, os outros 7 testes passaram intactos | [HttpWebsiteProbe](../src/AutoHous.Revenue.WebAudit/HttpWebsiteProbe.cs) |

**Como manter:** ao mergear uma fatia, acrescente uma linha aqui e atualize o
HANDOFF. As duas coisas juntas são o que faz a próxima sessão começar sabendo.
