# HANDOFF — onde paramos

> **Leia este arquivo antes de começar qualquer trabalho neste repositório.**
> Ele é a fonte da verdade sobre o estado atual: o que está pronto, o que está no
> meio do caminho e o que decidimos deliberadamente não fazer.
>
> **Ao terminar uma sessão de trabalho, atualize-o.** Um handoff desatualizado é
> pior que nenhum — ele faz a próxima pessoa confiar em algo que já mudou.

> Para achar onde fica cada coisa, o índice é o [docs/mapa.md](docs/mapa.md).

**Última atualização:** 01/09/2026
**Branch:** `main` — a `feat/website-auditor-e-carga-receita` foi mergeada (fast-forward)

---

## 1. Estado em uma tela

| | |
|---|---|
| Testes | **550/550** verdes |
| Migrations aplicadas no banco de dev | **17** de 17 — em dia |
| Carga da Receita `2026-08` | ✅ **concluída** |
| Website Auditor (A03) | ✅ fatia completa, testada ponta a ponta |
| Orchestrator (A01), Product Matcher (A04), People Finder (A05) | ✅ revisados, seis defeitos corrigidos — ver §5 |
| Hermes: credencial, gateway, MCP | ✅ verificados — ver §6 |
| Hermes real (`AGENT_RUNTIME=hermes`) | ❌ nunca executou — a chave ainda não foi virada |
| Railway | ❌ escrito, nunca construído |

---

## 2. A base está carregada

```
accounts                 677.999
companies_cnpj           712.904
companies_raw            839.409
account_locations        711.109
account_merge_candidates 126.403   ← fila de revisão humana
rf_cnae_stats            178.869
rf_municipio_stats       100.050
```

Origem: Dados Abertos CNPJ, release `2026-08`, 72.789.638 estabelecimentos lidos,
839.409 no universo automotivo, 808.043 casados com o arquivo `Empresas`.

**⚠ O quality gate de agrupamento não foi atingido: 82,2% contra os 85% que o
frame 04 pede.** Não é falha — os dados estão lá —, mas 126 mil linhas aguardam
decisão humana em `/merge-candidates`. Vale investigar se o limiar de 0,75 está
adequado para nomes brasileiros antes de aceitar esse número como normal.

O cache dos 6,7 GB está em `~/ah/.receita-cache` **dentro do WSL**, não no
repositório.

---

## 3. Ambiente: rode tudo no WSL

Não é preferência. São três fatos desta máquina:

1. **O Smart App Control bloqueia DLL recém-compilado.** Depois de qualquer build,
   o binário novo não tem reputação e o Windows recusa carregá-lo —
   `An Application Control policy has blocked this file`. Matou o Ingestor e os
   executáveis de teste.
2. `scripts/hermes-setup.sh` depende de `chmod 600` de verdade; no Git Bash é
   encenação.
3. O instalador e o gateway do Hermes são bash + Python.

```bash
wsl -d Ubuntu-24.04
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"   # já no .bashrc
cd /mnt/d/projects/autohousCommercialAutomation
```

O Postgres continua no Docker Desktop e é alcançável do WSL em `localhost:5433`
sem configuração extra.

**Standby está desativado** para a carga longa não morrer. Reverta quando não
precisar mais:

```powershell
powercfg /change standby-timeout-dc 60
```

**Nunca passe `--nologo` para `dotnet test`** — no modo MTP a flag faz o host
localizar os módulos e não iniciar nenhum, reportando "Zero tests ran" com código
5. Use `./scripts/test.sh`, que fixa a invocação correta.

---

## 4. O que foi entregue nesta sessão

### 4.1 Website Auditor (A03) — completo

Fatia vertical inteira, espelhando a forma do Researcher:

```
migration 0015   technologies (a P1 que nunca existiu), website_audit_evidence
                 (paga a dívida do array evidence_ids), multiple_portals e
                 complex_integration (que o OpportunityScoring JÁ LIA e o schema
                 não tinha)
domínio          WebsiteProbe, WebsiteAuditProfile, WebsiteAuditScoring,
                 sobrecarga do EvidenceFirstGuard
application      IWebsiteProbe, ExecuteWebsiteAuditUseCase, RequestWebsiteAuditUseCase
agents           WebsiteAuditPromptBuilder + validador multi-schema
WebAudit         projeto novo: HttpWebsiteProbe, TechnologySignatures
infra            WebsiteAuditPersister
api              POST /accounts/{id}/audit
hermes/          schema, prompt versionado, skill
```

**A decisão de desenho que importa:** sonda **mede** (performance, SEO, mobile,
tracking), agente **observa com evidência** (UX, conversão, estoque), plataforma
**pontua**. Deixar o LLM chutar `performance_score` contradiz a regra central.

A sonda HTTP não vê conteúdo renderizado por JavaScript — numa vitrine em SPA
isso é o estoque inteiro. Por isso a contagem de veículos é pergunta para o
**agente**, nunca para a sonda. Um headless browser entra depois atrás da mesma
porta `IWebsiteProbe`, sem tocar no resto.

### 4.2 Dois defeitos que teriam quebrado a ativação do Hermes

Ambos **invisíveis sob fixture e fatais em produção** — é o preço de um runtime
determinístico.

**Transporte.** No Hermes v0.21.0, `GET /v1/runs/{id}` **não devolve o texto
final**. Conferi na fonte instalada: nenhuma chamada a `_set_run_status` passa
`output`, e o texto só existe no evento `assistant.completed` da SSE, numa fila
sem histórico — quem faz polling chega tarde. O sintoma seria `RawText` vazio em
100% dos runs, reprovado como `contract_violation`, cuja leitura óbvia é "o
modelo não formata JSON". A investigação começaria pelo prompt.
→ `HermesOptions.Transport` agora é `Chat` por padrão, e o caminho `/v1/runs`
falha alto dizendo o remédio.

**Fuso.** O Npgsql recusa `DateTimeOffset` com offset diferente de zero em
`timestamptz`. Um agente pesquisando empresa brasileira devolve `observed_at` em
`-03:00` com naturalidade, e isso atravessa schema, guard e desserialização sem
um arranhão — falha só no `INSERT`. Os fixtures usavam `Z`.
→ `Infrastructure/Timestamps.cs`, aplicado nos dois persisters e fixado por
regressão nos dois fixtures.

### 4.3 Três defeitos de escala na carga

Mesma assinatura: **invisíveis na escala em que o código foi escrito, dominantes
na escala em que ele roda.** Detalhe completo em
[docs/carga-receita-otimizacao.md](docs/carga-receita-otimizacao.md).

| Defeito | Medição | Correção |
|---|---|---|
| Limiar do `%` (0.30) divergindo do filtro (0.75) | 147 → 11,6 ms | `set_limit()` por conexão |
| FK `raw_id` sem índice | `DELETE` de 839k rodou 10 min | migration `0016` |
| **Cast implícito anulando índice** | **50,7 → 0,076 ms** | `cast(@Cnpj as char(14))` |

O terceiro é o mais instrutivo: `companies_cnpj.cnpj` é `character(14)`, o Dapper
manda `string`, o Npgsql tipa como `text`, e o Postgres reescreve para
`(cnpj)::text = $1::text` — **o cast do lado da coluna, que nenhum índice serve**.
Resultado: 96 → 31 ms por linha, ETA de 12 h para 3h44.

**Onde mais isso pode estar:** todas as colunas `character` do schema —
`companies_cnpj.uf`, `company_partners.cnpj_basico`,
`account_merge_candidates.incoming_cnpj/uf`, `accounts.state`,
`account_locations.state`. Corrigi as quatro comparações que existiam; consultas
novas sobre essas colunas precisam do mesmo cuidado.

### 4.4 Resiliência e retomada

- `--resolve-batch <uuid>` no Ingestor: retoma um lote já capturado, pulando
  download, leitura e captura. Foi o que salvou ~4 horas hoje.
- Retry com espera crescente em `NpgsqlConnectionFactory.OpenAsync` (5×, 1s→8s).
  A `/health` passa `retryTransient: false` — uma liveness probe que insiste 15 s
  faria o Railway reiniciar um serviço que só esperava o banco.
- `.gitattributes` com `eol=lf`: `core.autocrlf=true` deixava os `.sh` em CRLF, o
  que mataria `deploy/hermes-entrypoint.sh` no container com `bash\r: No such
  file` — erro que não menciona fim de linha em lugar nenhum.

### 4.5 Railway

`deploy/Dockerfile.{api,worker,hermes}`, `hermes-entrypoint.sh`, três
`railway/*.json` e [docs/deploy-railway.md](docs/deploy-railway.md).

**A regra que não pode ser afrouxada:** o `hermes-gateway` **não pode ter Public
Networking**. O API Server expõe a superfície completa de ferramentas, incluindo
execução de terminal. Sem domínio público a única rota é a rede privada; com ele,
um Bearer seria a única coisa entre a internet e um shell.

---

## 5. A revisão de A01, A04 e A05 — feita

O handoff anterior registrava esses três como "código presente, não revisado".
**Foram revisados linha a linha.** Seis defeitos corrigidos, cinco regressões
novas, 550/550 verdes.

A dúvida que o handoff levantava — se os repositórios novos sofriam do cast
implícito da §4.3 — **não se confirmou**. As sete colunas `character(n)` do
schema não são tocadas pelo código novo; o único acesso a `companies_cnpj` filtra
por `account_id` (uuid), com Index Only Scan confirmado.

O que estava errado era outra coisa:

| | Defeito | Correção |
|---|---|---|
| 1 | **Laço infinito de pesquisa.** O ramo de completude baixa devolvia `Research` sem consultar `next_research_at`. A cadeia se alimentava sozinha — `research.completed` → `Research` → `research.completed` — sem evento externo nenhum, sem erro e sem run falhado. E a completude é declarada pelo **próprio agente**: a conta sem presença digital devolve 0,25 com honestidade, então o laço caía justamente onde a pesquisa rende menos. | `AccountOrchestration` respeita o cooldown; conta rasa no prazo vai para `Nurture` |
| 2 | **Safra de fit instável.** `product_fit.calculated_at` tem default `now()`, que é o início da **transação**: as cinco linhas de uma safra têm o mesmo timestamp. O `order by calculated_at desc limit 1` da view não desempatava, e `product_fit_batch_id` oscilava — corroendo em silêncio a chave `contacts:{conta}:{safra}` | lateral da `0017` ordena por `calculated_at desc, recommended_entry desc, id desc` |
| 3 | **AutoFollow somava 95** contra 100 dos outros quatro, e `RecommendedEntry` os compara por `Score`: desconto estrutural na disputa pela porta de entrada | `captura_sem_destino` foi para 35 |
| 4 | **Run órfão.** O `research_run` nascia antes do `EnqueueAsync`, cujo retorno era descartado — e ele faz `on conflict do nothing`. Comando descartado deixava um run `queued` que prendia a conta em `Wait` para sempre: não há lease no claim do outbox nem varredura de run velho | enfileira primeiro, cria o run só se o comando entrou |
| 5 | **Desqualificador eterno e duplicado.** Sem `expires_at` e sem dedupe: bloqueio permanente sem endpoint de revisão, mais uma cópia a cada safra | safra nova vence a anterior; horizonte de 180 dias |
| 6 | **Fit abaixo do corte gastava o People Finder.** `ProductFitAt` fica preenchido mesmo quando nenhum produto passa do corte; o corte de tier só pega tier ≥ 4 | passo novo lendo `has_recommended_entry` |

**A `0017` foi editada, não emendada.** Os itens 2 e 6 mexem na view
`v_account_progress`, e isso só foi legítimo porque a migration nunca tinha
rodado. Depois de aplicada, a mesma correção custaria uma `0018`.

### O que a revisão deixou em aberto

- **Um teste de integração intermitente.** Uma falha em seis execuções, não
  reproduzida em quatro tentativas seguintes, teste não identificado.
  `OutboxConcurrencyTests` é o suspeito natural. Não é do diff da revisão — o
  vermelho apareceu numa build cujas mudanças as verdes seguintes também tinham.
- **`ProductFitPersister` não tem teste de integração.** Nenhum SQL dele toca um
  Postgres real na suíte. Foi por isso que o horizonte do desqualificador é `int`
  de dias multiplicado por `interval '1 day'`, e não `TimeSpan`: não quis apoiar
  em inferência de tipo do Npgsql o que nenhum teste pegaria. `WebsiteAuditSliceTests`
  é o molde pronto para pagar isso.
- **`v_account_current_fit` é `select *` congelado na 0009** — não expõe
  `coverage`, `pitch_confidence` nem `account_score_id`, que a `0017` acrescentou.
  Funciona hoje porque `GetCurrentAsync` só lê colunas antigas; é armadilha para a
  próxima consulta.
- **A01, A04 e A05 não têm superfície HTTP nenhuma.** Nada lê `product_fit`,
  `contacts` ou `v_account_progress`, e não há como disparar o Orchestrator à mão.
  Três agentes que só existem dentro da cadeia do outbox, sem janela de inspeção —
  e é isso que torna o item 4 acima latente em vez de fatal hoje.
- **Duas contas já estão presas em `Wait` no banco de dev.** Aplicada a `0017`,
  `v_account_progress` mostra `has_run_in_flight = true` para duas contas cujos
  `research_runs` estão em `queued` desde 31/08 18:00 e 01/09 01:20 — nunca
  começaram, nunca terminaram. São de pedidos manuais anteriores ao Orchestrator,
  então não foram ele que as criou; mas agora que o passo 2 da decisão lê esse
  campo, **essas duas contas não andam mais**. É o mesmo buraco do item 4 visto
  do outro lado: `ClaimBatchAsync` marca `processing` sem lease e nada varre run
  velho, então `queued` órfão é estado terminal. Um reaper de `research_runs`
  parados resolve as duas coisas de uma vez.

---

## 6. Hermes: o que já está pronto

**Os três primeiros passos do handoff anterior já estavam feitos** — ele estava
desatualizado. Verificado em 01/09/2026:

| | |
|---|---|
| Credencial de modelo | ✅ `hermes doctor` → "Nous Portal auth (logged in)"; modelo `gpt-5.6-luna-900k` via `openai-codex` |
| `HERMES_API_SERVER_KEY` no `.env` | ✅ 48 chars (não vazia) |
| MCP publicado em `~/.local/share/autohous/revenue-mcp` | ✅ |
| `skills.external_dirs` apontando para `hermes/skills/` do repo | ✅ |
| Gateway em `:8642` | ✅ `{"status":"ok","version":"0.21.0"}` |
| API em `:5080`, com 401 sem Bearer | ✅ |
| **Allowlist do MCP** | ✅ exatamente 3 ferramentas de leitura |

**`hermes mcp test autohous_revenue` NÃO funciona — e não é o MCP.** Ele falha
com `Connection failed: Connection closed` em ~8 s, de qualquer diretório, com
stdin aberto ou fechado. O servidor está são: rodado direto, faz `initialize` e
responde `tools/list` com `get_account_context`, `list_account_evidence` e
`get_product_catalog`. stdout está limpo (os logs vão para stderr), então não é
corrupção de protocolo.

O runbook manda parar se o `hermes mcp test` listar mais de três ferramentas —
**mas ele não serve como porteiro aqui**. Use o handshake manual:

```bash
~/mcp-probe.sh    # initialize + tools/list, JSON-RPC na mão
```

### Ensaio em fixture — a cadeia do Orchestrator roda

`POST /accounts/{id}/research?force=true` na conta do fixture
(`grupoventosul.com.br`) encadeou sozinho, tudo `processed`, zero erro:

```
research.requested → research.completed → score.requested → score.ready → Nurture
```

E parou no lugar certo, com justificativa no log:

```
Orchestrator decidiu Score:    fato novo em 2026-09-02 02:19 posterior ao score de 2026-09-01 01:25
Orchestrator decidiu Nurture:  tier 4: abaixo do corte para produto e contato
```

**A01 e A03 estão exercitados ponta a ponta. A04 e A05 não, e não dá com este
fixture:** a conta pontua 41,65, e tier 3 começa em 50. O corte frio do passo 6
recusa gastar Product Matcher e People Finder nela — comportamento correto, mas
significa que `ProductFitPersister` e `ContactPersister` **nunca escreveram numa
base real**, nem em fixture. É a mesma lacuna da §5, vista de outro ângulo.

---

## 7. Próximos passos, em ordem

1. **Exercitar A04 e A05 em fixture.** Precisa de uma conta que pontue ≥ 50 — o
   fixture do researcher sempre devolve o mesmo perfil frio. Sem isso, virar a
   chave leva dois persisters não testados direto para produção.
2. **Virar `AGENT_RUNTIME=hermes`** e fazer o primeiro run real. O que se olha
   primeiro é `agent_runs`: se todo run falhar como `contract_violation`, veja o
   transporte antes do prompt (§4.2).
3. **Validar `docker build -f deploy/Dockerfile.hermes`** — nunca foi construído.
4. **Olhar o quality gate de 82,2%** — 126 mil linhas em revisão é muito.
5. **Pagar a cobertura de integração do `ProductFitPersister`** e caçar o teste
   intermitente da §5.

---

## 8. Decisões que tomamos e não devem ser refeitas sem motivo

- **Índice composto por UF no trigrama: rejeitado.** Parece a otimização óbvia e
  não é. O corte real é ~3,8x (SP concentra 26,6% das contas), e custa **29,3% da
  fila de revisão** — nome parecido em UF diferente não é descartado, vira
  `name_match_other_uf` e vai para humano. É decisão de produto, não otimização.
- **Lote de transações na resolução: rejeitado sem medição.** O caso de uso
  escolheu uma transação por linha de propósito; o teto de ganho é baixo.
- **`Max Auto Prepare`: pendente, marginal.** Planejar custa 1,66 ms contra 1,13
  de execução. Valeria ~10–15% hoje, mas exige reinício.
