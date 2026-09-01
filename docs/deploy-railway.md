# Deploy no Railway

Quatro serviços em um projeto, três deles sem endereço na internet.

```
                    internet
                        │
                        ▼  (TLS terminado pelo proxy do Railway)
              ┌───────────────────┐
              │   revenue-api     │  ← ÚNICO com Public Networking
              │   :8080           │     Bearer do ADR-0009 em tudo menos /health
              └─────────┬─────────┘
                        │
      ┌─────────────────┼─────────────────────────────┐
      │   rede privada do projeto (*.railway.internal, IPv6)
      │                 │                             │
┌─────▼──────┐   ┌──────▼────────┐            ┌───────▼────────┐
│  Postgres  │◄──┤ revenue-worker├───────────►│ hermes-gateway │
│  :5432     │   │ (não escuta)  │  /v1/runs  │ :8642          │
└────────────┘   └───────────────┘            └───────┬────────┘
                                                      │ stdio
                                              ┌───────▼────────┐
                                              │ Revenue MCP    │
                                              └───────┬────────┘
                                                      │ HTTP, 3 ferramentas de leitura
                                                      └──► revenue-api (rota privada)
```

## A regra que não pode ser afrouxada

**`hermes-gateway` não pode ter Public Networking habilitado.**

O `HERMES.md` resolve isto localmente com *"manter em 127.0.0.1"*. Dentro de um
container essa frase perde o sentido que tinha: o processo já está sozinho no seu
namespace de rede, e o loopback não separa mais nada. O que separa aqui é a
ausência de domínio público — sem ele, a única rota até a porta 8642 vem de dentro
do projeto.

Com domínio público, um Bearer de 48 hex passaria a ser a única coisa entre a
internet e a superfície completa de ferramentas do API Server, **incluindo
execução de terminal**. Não é uma superfície que se protege com uma chave só.

Isso é também o motivo de o gateway ligar em `::` e não em `127.0.0.1`: a rede
privada do Railway é IPv6-only, e um socket no loopback não atenderia o worker.
A fronteira mudou de endereço; ela não deixou de existir.

## Por que a stack inteira, e não só o agente

O MCP roda como subprocesso stdio **dentro** do container do gateway, e fala HTTP
com a Revenue API. Se a API ficasse na sua máquina, esse subprocesso precisaria de
um túnel reverso até o seu `localhost` — e o segredo da API passaria a viajar para
fora, invertendo exatamente a fronteira que o `HERMES.md` descreve. Manter os
quatro no mesmo projeto é o que mantém `REVENUE_API_URL` num endereço privado.

## Serviços

Todos apontam para o mesmo repositório; o que os distingue é o arquivo de config.
Em cada serviço, **Settings → Config as code**, aponte para:

| Serviço | Config as code | Public Networking |
|---|---|---|
| `revenue-api` | `deploy/railway/api.json` | **habilitado** |
| `revenue-worker` | `deploy/railway/worker.json` | desabilitado |
| `hermes-gateway` | `deploy/railway/hermes.json` | **desabilitado — ver acima** |
| `Postgres` | — (plugin gerenciado) | desabilitado |

### Variáveis compartilhadas do projeto

Em **Project Settings → Shared Variables**, para que as duas pontas de cada
integração leiam o mesmo valor sem ninguém copiar segredo entre telas:

```bash
REVENUE_API_KEY=$(openssl rand -hex 24)          # ADR-0009, piso de 24 chars
HERMES_API_SERVER_KEY=$(openssl rand -hex 24)    # piso de 16 chars do Hermes
```

Rotação sem downtime é o motivo de `RevenueApiKeys` aceitar lista:
`REVENUE_API_KEY=nova,antiga` cobre a janela, e depois some com a antiga.

### `revenue-api`

```bash
PORT=8080          # explícito de propósito: é o que torna o endereço privado previsível
REVENUE_DB_CONNECTION=Host=${{Postgres.RAILWAY_PRIVATE_DOMAIN}};Port=5432;Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}
REVENUE_API_KEY=${{shared.REVENUE_API_KEY}}
```

As migrations rodam em `preDeployCommand`, no mesmo container e antes de a versão
entrar no ar. Migration que falha aborta o deploy em vez de publicar uma API que
fala com um schema que não existe.

### `revenue-worker`

```bash
REVENUE_DB_CONNECTION=Host=${{Postgres.RAILWAY_PRIVATE_DOMAIN}};Port=5432;Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}
AGENT_RUNTIME=fixture                            # vire para "hermes" só depois do smoke test abaixo
HERMES_BASE_URL=http://${{hermes-gateway.RAILWAY_PRIVATE_DOMAIN}}:8642
HERMES_API_SERVER_KEY=${{shared.HERMES_API_SERVER_KEY}}
```

`REPOSITORY_ROOT` e `AGENT_FIXTURE_DIR` já vêm fixados na imagem — o worker
resolve prompt, schema e fixtures de `/contracts`, e não do disco de um repo que
não existe no container.

Subir primeiro com `AGENT_RUNTIME=fixture` é deliberado: exercita o caminho de
produção inteiro — outbox, validador, guard, persistência transacional, scoring —
sem gastar um centavo de modelo e sem depender do gateway estar de pé. Se a
pesquisa falha em fixture, o problema não é o Hermes.

**`numReplicas` fica em 1 por enquanto, mas não por necessidade:** o outbox
reivindica com `FOR UPDATE SKIP LOCKED`, então N réplicas dividem a fila sem
processar o mesmo evento duas vezes. Subir esse número é seguro quando a fila
justificar.

### `hermes-gateway`

```bash
PORT=8642
REVENUE_API_URL=http://${{revenue-api.RAILWAY_PRIVATE_DOMAIN}}:8080
REVENUE_API_KEY=${{shared.REVENUE_API_KEY}}
HERMES_API_SERVER_KEY=${{shared.HERMES_API_SERVER_KEY}}
HERMES_PORTAL_API_KEY=...                        # credencial de modelo do Nous Portal
```

`REVENUE_API_URL` no domínio **privado**: o `hermes-entrypoint.sh` avisa em stderr
se o endereço não for da rede interna, porque apontar para o domínio público faria
o tráfego do MCP sair para a internet e voltar, com a chave junto.

O entrypoint materializa `REVENUE_API_KEY` num arquivo `0600` e passa ao MCP o
**caminho**, não o valor — o `config.yaml` não interpola `${VAR}` no bloco `env:`,
e o processo do gateway é justamente o que tem ferramenta de terminal exposta, de
modo que a chave fora do `/proc/{pid}/environ` dele é ganho real.

## Ordem de subida

O primeiro deploy tem uma dependência circular aparente — o gateway quer
`revenue-api.RAILWAY_PRIVATE_DOMAIN`, que só existe depois de a API subir. Não é
circular de verdade: o MCP só conecta na primeira ferramenta chamada.

1. `Postgres` (plugin).
2. `revenue-api`. O `preDeployCommand` aplica as 14 migrations; o log deve dizer
   quantos scripts foram aplicados. **`0 script(s)` num banco vazio é falha, não
   sucesso** — significa que os `.sql` não foram embarcados no publish.
3. `revenue-worker` com `AGENT_RUNTIME=fixture`.
4. `hermes-gateway`.
5. Só então vire o worker para `AGENT_RUNTIME=hermes`.

## Smoke test

```bash
API=https://<seu-dominio>.up.railway.app
KEY=<REVENUE_API_KEY>

curl -s $API/health                                    # aberto, sem Bearer
curl -s $API/accounts -H "Authorization: Bearer $KEY"   # exige Bearer
curl -s -o /dev/null -w '%{http_code}\n' $API/accounts  # deve dar 401
```

O gateway não tem domínio público, então o teste dele é de dentro:
`railway run --service hermes-gateway curl -s localhost:8642/health`.

## O que este deploy NÃO resolve

- **Autorização por consumidor.** Quem tem a `REVENUE_API_KEY` alcança toda a
  superfície, inclusive a de escrita. O gatilho de revisão está no
  [ADR-0009](adr/0009-credencial-de-borda-da-revenue-api.md), e este deploy não o
  desarma — ele só troca "Bearer em claro sobre HTTP local" por "Bearer sobre TLS
  do proxy", que resolve o transporte e não a autorização.
- **Custo de modelo.** Não há teto por conta nem por dia. `agent_runs.estimated_cost`
  registra o gasto depois do fato; nada o impede antes.
- **Backup do Postgres.** O plugin do Railway tem snapshot próprio; ele não está
  configurado aqui, e `companies_raw` depois de uma carga nacional não é algo que
  se reconstrói em uma tarde.
- **A carga da Receita.** O Ingestor é CLI e não sobe como serviço. Uma carga
  nacional são 7,3 GB baixados e ~63 milhões de linhas lidas: rode por
  `railway run --service revenue-api dotnet ...` ou de uma máquina com a
  `REVENUE_DB_CONNECTION` do projeto, e não como parte de um deploy.
