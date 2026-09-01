# Rodar o Hermes — passo a passo

Do zero até `AGENT_RUNTIME=hermes` com um run real gravando no banco.

> **Tudo aqui roda no WSL, não no Windows.** Não é preferência de ambiente — é o
> que funciona nesta máquina. Ver [Por que WSL](#por-que-wsl) no fim.

---

## 0. Pré-requisitos

Confira antes de começar; cada linha abaixo já está satisfeita neste ambiente:

```bash
wsl -d Ubuntu-24.04
dotnet --version          # 10.0.400
hermes --version          # Hermes Agent v0.21.0
docker ps                 # autohous-revenue-db (healthy)
```

Se `dotnet` não for encontrado, o `PATH` está no `~/.bashrc`:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

---

## 1. Credencial de modelo — só você pode fazer

```bash
hermes setup --portal     # login no Nous Portal (interativo)
hermes model              # escolher o modelo
hermes doctor             # diagnóstico
```

**Sem isto o gateway sobe e todo run falha na primeira chamada de modelo.** É a
única etapa que não dá para automatizar: o login é interativo e a credencial é sua.

> O `hermes setup --portal` lê do `/dev/tty` diretamente. Se você o executar de
> dentro de um script ou de uma sessão sem terminal, ele trava sem mensagem — foi
> exatamente o que aconteceu na instalação, e o contorno lá foi `setsid`. **Rode
> este comando num terminal de verdade.**

---

## 2. Ligar o Hermes ao Revenue Engine

```bash
cd /mnt/d/projects/autohousCommercialAutomation
./scripts/hermes-setup.sh
```

Idempotente — rodar de novo não duplica nada. O que ele faz, e por quê:

| Passo | Por quê |
|---|---|
| publica o MCP em `~/.local/share/autohous/revenue-mcp` | não pode ser `dotnet run`: a saída de build corromperia o protocolo stdio |
| gera `API_SERVER_KEY` em `~/.hermes/.env` | **é a presença desta chave que LIGA o API Server**, não `API_SERVER_ENABLED` |
| grava o segredo da Revenue API em arquivo `0600` | o `config.yaml` tem 0644 e não interpola `${VAR}` no bloco `env:` do MCP |
| aponta `skills.external_dirs` para `hermes/skills/` do repo | a skill versionada é a que roda — cópia envelhece em silêncio |
| limita `delegation` a 3 filhos e 1 nível | árvore rasa antes de árvore funda |
| espelha as chaves no `.env` do projeto | os dois lados leem o mesmo valor sem ninguém copiar segredo entre arquivos |

Ao terminar, confira que a chave saiu do vazio:

```bash
grep HERMES_API_SERVER_KEY .env
```

---

## 3. Subir a stack, na ordem

O Hermes precisa alcançar a Revenue API, então ela sobe primeiro. Três terminais,
ou `tmux`:

```bash
cd /mnt/d/projects/autohousCommercialAutomation
set -a; . ./.env; set +a          # as aspas no .env não são enfeite — ver .env.example
```

```bash
# terminal 1 — banco (se ainda não estiver de pé)
docker compose up -d db

# terminal 2 — API, porta 5080
dotnet run --project src/AutoHous.Revenue.Api

# terminal 3 — gateway do Hermes, porta 8642
hermes gateway

# terminal 4 — worker
dotnet run --project src/AutoHous.Revenue.Worker
```

> **A API precisa estar no WSL.** O `config.yaml` aponta o MCP para
> `http://127.0.0.1:5080`. Uma API rodando no Windows **não** é alcançável desse
> endereço a partir do WSL — o `localhost:5433` do Postgres só funciona porque o
> Docker Desktop projeta a porta na distro; um `dotnet run` no Windows não tem
> esse tratamento.

---

## 4. Conferir antes de gastar dinheiro

```bash
# gateway no ar
curl -s http://127.0.0.1:8642/health

# o gateway enxerga o modelo
curl -s http://127.0.0.1:8642/v1/models \
  -H "Authorization: Bearer $HERMES_API_SERVER_KEY"

# o MCP conecta e expõe EXATAMENTE três ferramentas de leitura
hermes mcp test autohous_revenue

# a API exige Bearer em tudo menos /health (ADR-0009)
curl -s http://127.0.0.1:5080/health
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5080/accounts     # 401
curl -s 'http://127.0.0.1:5080/search/accounts?q=comercio&limit=2' \
  -H "Authorization: Bearer $REVENUE_API_KEY"
```

Se `hermes mcp test` listar mais de três ferramentas, a allowlist do
`config.yaml` não foi aplicada — **pare aqui**. O filtro de ferramentas do Hermes
é por *servidor*, não por agente: aquela allowlist é a única fronteira efetiva
entre o modelo e a superfície de escrita da API.

---

## 5. Ensaiar sem custo, primeiro

```bash
# .env
AGENT_RUNTIME=fixture
```

Com `fixture`, o caminho de produção inteiro roda — outbox, validador, guard,
persistência transacional, scoring — lendo respostas gravadas de
`tests/fixtures/agent-runs/`. **Custo zero, resultado determinístico.**

```bash
API=http://localhost:5080
api() { curl -s -H "Authorization: Bearer $REVENUE_API_KEY" "$@"; }

# Nao existe `GET /accounts` - o verbo em /accounts e POST, de criacao.
# Listar e buscar e /search/accounts, que exige `q`.
#
# NAO pegue `uma conta qualquer`: o fixture do researcher devolve sempre o
# mesmo dominio (grupoventosul.com.br) e `accounts.domain` tem indice unico.
# Numa base onde alguem ja rodou a pesquisa, qualquer outra conta morre em
# 23505 - e voce nao ve nada, porque a API ja devolveu 202. Use a conta que
# ja e dona daquele dominio, e o run vira idempotente:
# O dominio VAI INTEIRO na busca: o `domain` entra no search_vector com peso B,
# mas o tokenizador guarda 'grupoventosul.com.br' como um unico token do tipo
# host - `q=grupoventosul` nao casa com nada.
ID=$(api "$API/search/accounts?q=grupoventosul.com.br&limit=1" | jq -r '.[0].id')
# Em base limpa, qualquer conta sem dominio serve.

# ?force=true porque um run anterior da mesma conta faz o POST responder 409.
api -X POST "$API/accounts/$ID/research?force=true"   # pesquisa
api -X POST $API/accounts/$ID/audit         # auditoria de site (A03)

api $API/accounts/$ID/evidence
api $API/accounts/$ID/cost
```

Se falhar em fixture, **o problema não é o Hermes.**

---

## 6. Virar a chave

```bash
# .env
AGENT_RUNTIME=hermes
```

Reinicie o worker. O primeiro run real é o momento de olhar:

```sql
select agent_name, prompt_version, model_name, status,
       input_tokens, output_tokens, estimated_cost, error
from agent_runs order by started_at desc limit 5;
```

---

## Quando der errado

### O gateway sobe mas nada responde em :8642

`API_SERVER_KEY` fraca ou ausente. O gateway carrega a plataforma `api_server`
apenas quando encontra uma chave **utilizável** — 16 caracteres ou mais e fora da
lista de placeholders. Com chave fraca ele **não reclama**: sobe sem a plataforma,
e o watcher de reconexão gira em erro. O `hermes-setup.sh` gera 48 hex.

### `MSB3021: ... Access to the path ... is denied` no build

Nao e permissao do WSL. E um processo do **Windows** rodando dos mesmos
`bin/Debug/net10.0` e segurando os DLLs abertos — tipicamente uma API ou Worker
que voce subiu antes de migrar para o WSL. Os dois lados compartilham a arvore
em `D:`, entao o build no WSL nao consegue sobrescrever o que o Windows travou.

```powershell
Get-Process | Where-Object { $_.ProcessName -like '*AutoHous*' }
Stop-Process -Id <pid> -Force
```

O mesmo processo costuma ser a causa de um sintoma pior e mais silencioso: a API
responde em `localhost:5080` **do Windows**, o `curl` daqui funciona, e voce
conclui que a stack esta de pe. O MCP no WSL bate em `127.0.0.1:5080` e nao acha
ninguem. Confira de que lado a porta esta escutando antes de investigar o Hermes.

### `400 Bad Request` com corpo vazio em toda chamada autenticada

Kestrel rejeitando no nivel do protocolo, antes da autenticacao — por isso nao ha
corpo, nem log de 401. A causa quase sempre e um `\r` no valor do header:
o `.env` foi salvo com CRLF, e `set -a; . ./.env` traz o `\r` dentro da
variavel. `Authorization: Bearer <chave>\r` nao e header valido.

O `.env` e gitignored, entao o `* text=auto` do `.gitattributes` nunca o alcanca.

```bash
file .env                                   # 'with CRLF' e o diagnostico
printf '%s' "$REVENUE_API_KEY" | od -c | tail -2   # procure o \r
sed -i 's/\r$//' .env                        # conserto
```

A API (`Split(',', TrimEntries)`) e o MCP (`File.ReadAllText(path).Trim()`)
toleram o `\r` do lado deles; quem quebra e o `curl` do shell.

### Todo run falha como `contract_violation`

A leitura óbvia é "o modelo não sabe formatar JSON". **Confira o transporte antes
do prompt.**

No Hermes v0.21.0, `GET /v1/runs/{id}` **não devolve o texto final** — nenhuma
chamada a `_set_run_status` passa `output`, e o texto só existe no evento
`assistant.completed` da SSE, numa fila sem histórico. Quem faz polling chega
depois de o texto ter passado.

Por isso `HermesOptions.Transport` é **`Chat`** por padrão. Se alguém tiver mudado
para `Runs`, o cliente agora falha alto dizendo isso — mas confira:

```csharp
// src/AutoHous.Revenue.Agents/HermesOptions.cs
public HermesTransport Transport { get; set; } = HermesTransport.Chat;
```

### O POST responde 202 e nada acontece (`23505 accounts_domain_uq`)

Em `fixture`, o researcher devolve sempre `grupoventosul.com.br`. Como
`accounts.domain` e unico, a segunda conta que voce mandar pesquisar colide no
INSERT. A API ja respondeu 202 nesse ponto — a falha so existe no worker, e o
evento morre depois de 5 tentativas:

```sql
select event_type, status, attempts, last_error
from events_outbox order by created_at desc limit 5;
```

Rode na conta que ja e dona do dominio do fixture (ver passo 5), ou limpe o
dominio da conta anterior. Nao e defeito do pipeline: e o preco de um fixture
deterministico contra uma coluna unica.

### O evento não processa e a tabela de destino fica vazia

O `OutboxDispatcher` captura toda exceção e a guarda antes de reagendar:

```sql
select event_type, attempts, status, last_error
from events_outbox where last_error is not null
order by created_at desc limit 10;
```

### `Cannot write DateTimeOffset with Offset=-03:00:00`

Já corrigido (`Infrastructure/Timestamps.cs`), mas vale saber por quê: o Npgsql
recusa offset diferente de zero em `timestamptz`. Um agente pesquisando empresa
brasileira devolve `observed_at` em `-03:00` com naturalidade, e essa string
atravessa schema, guard e desserialização sem um arranhão — a falha só aparece no
`INSERT`. Os fixtures usavam `Z`, então a bateria passava verde sobre um caminho
que nunca teria funcionado com o Hermes real.

### `hermes mcp test` não conecta

O MCP é publicado, não executado por `dotnet run`. Republique:

```bash
./scripts/hermes-setup.sh
```

### Algum comando trava sem mensagem

O instalador e o wizard do Hermes leem do `/dev/tty` direto, driblando
redirecionamento de stdin. Num script ou sessão sem terminal isso trava para
sempre. Contorno:

```bash
setsid bash o-script.sh < /dev/null
```

---

## Por que WSL

Três fatos desta máquina, todos descobertos na prática:

1. **O Smart App Control bloqueia DLL recém-compilado do projeto.** Não é
   intermitente: depois de qualquer build, o binário novo não tem reputação e o
   Windows recusa carregá-lo — `An Application Control policy has blocked this
   file`. Foi o que matou o Ingestor e os executáveis de teste. A API e o Worker
   que estiverem de pé só continuam funcionando porque rodam de binários antigos.
2. **O `hermes-setup.sh` depende de semântica POSIX real.** Ele grava o segredo
   com `chmod 600`, e o `HERMES.md` justifica o arquivo *pelo modo*. No Git Bash
   o `chmod` é encenação.
3. **O instalador e o gateway são bash + Python.**

O Postgres continua no Docker Desktop e é alcançável do WSL em `localhost:5433`
sem nenhuma configuração extra — testado.

---

## Ordem de leitura

- [HERMES.md](../HERMES.md) — o que o agente pode e o que não pode
- [docs/agents.md](agents.md) — a camada de agentes, o transporte e o pipeline de validação
- [docs/deploy-railway.md](deploy-railway.md) — o mesmo, em container
- [ADR-0009](adr/0009-credencial-de-borda-da-revenue-api.md) — a credencial de borda
