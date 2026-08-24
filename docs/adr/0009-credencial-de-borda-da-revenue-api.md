# ADR-0009 — Credencial de borda da Revenue API

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

A Revenue API nasceu sem autenticação. Em dev isso não incomodava: `localhost`,
um consumidor só, Postgres em container descartável.

Duas coisas mudaram no mesmo dia.

**O MCP já mandava credencial que ninguém conferia.** `RevenueApiClient` envia
`Authorization: Bearer $REVENUE_API_KEY` desde o início, e `Program.cs` não tinha
nenhum middleware lendo esse header. O `hermes/config/config.example.yaml`
documentava a variável. O contrato existia dos dois lados menos no que importa —
o lado que valida.

**O Hermes entrou de verdade.** O API Server dele expõe a superfície completa de
ferramentas ao modelo, incluindo execução de terminal. A partir daí, "aberto no
`127.0.0.1`" deixou de descrever uma rede confiável: passou a descrever um
processo que executa o que um LLM decidir executar.

E o alvo não é leitura. `POST /accounts`, `POST /accounts/{id}/research`,
`POST /merge-candidates/{id}/decide` e `POST /ingestion/batches` escrevem estado
comercial — e a pesquisa gasta dinheiro de modelo por chamada.

## Opções consideradas

1. **Continuar sem credencial e confiar na rede.** Funciona enquanto tudo for
   `localhost`. Em HML/PRD vira o segundo consumidor, o túnel de debug, o
   sidecar — e o dia em que a porta escapa não aparece em teste nenhum.
2. **JWT/OIDC completo.** Correto para uma API com usuários. Aqui os consumidores
   são dois processos nossos (MCP e, no futuro, um front interno); montar
   provedor de identidade para isso é infraestrutura sem dono.
3. **API key na borda, com falha fechada na subida.**

## Decisão

**Opção 3**, com quatro escolhas que só fazem sentido explicadas:

**Falha fechada na inicialização.** Sem chave utilizável — ausente, com menos de
24 caracteres, ou na lista de placeholders — o processo não sobe. A alternativa
(subir aberto e logar aviso) produz um serviço que responde 200, parece saudável
e só revela o buraco quando alguém de fora encontra. É o mesmo critério que o
gateway do Hermes aplica a si mesmo com `API_SERVER_KEY`, o que mantém uma regra
só nos dois lados da integração.

**Middleware, não filtro por endpoint.** Rota nova entra protegida por padrão. Um
filtro precisa ser lembrado em cada `MapPost`, e o esquecimento é silencioso.

**Lista de chaves.** `REVENUE_API_KEY=nova,antiga` faz as duas valerem ao mesmo
tempo. Sem isso, rotacionar credencial em PRD exige derrubar o consumidor —
e credencial que dá trabalho para trocar não é trocada.

**Arquivo antes de variável.** `REVENUE_API_KEY_FILE` tem precedência sobre
`REVENUE_API_KEY`. É o formato que Docker secrets e Kubernetes montam, e variável
de ambiente vaza em `docker inspect`, em `/proc/{pid}/environ` e em qualquer dump
de processo. Isso resolve também um detalhe do caminho do Hermes: o filtro de
ambiente dele só repassa ao subprocesso do MCP o que está declarado no bloco
`env:` do servidor, e não interpola `${VAR}` — sem o arquivo, a chave literal
teria de morar dentro de `~/.hermes/config.yaml`.

`/health` fica aberto. Probe de liveness de orquestrador roda sem credencial, e o
que ele devolve — "banco alcançável" — já é observável de fora pelo simples fato
de a API responder.

A comparação é em tempo fixo sobre o SHA-256 da chave, e sem short-circuit entre
as chaves ativas: `==` sai no primeiro byte diferente, e sair no primeiro acerto
contaria quantas chaves existem antes da que casou.

## Consequências

- Toda invocação da API — `curl` de operação, teste de integração, MCP — precisa
  do header. `WebApplicationFactory` configura a chave por `UseSetting`.
- O `.env.example` ganha `REVENUE_API_KEY` e `REVENUE_API_KEY_FILE`;
  `scripts/hermes-setup.sh` gera a chave, grava em `~/.hermes/secrets/`
  com permissão `0600` e aponta o MCP para o arquivo.
- **A chave viaja em claro.** Bearer sobre HTTP só é aceitável no laço local. Em
  HML/PRD a API precisa estar atrás de TLS — terminação no proxy resolve, exposição
  direta não.
- Autorização continua inexistente: quem tem a chave pode tudo. Para dois
  processos nossos isso é suficiente; para um front com usuários, não.

## Gatilho de revisão

Rever quando:

- entrar um consumidor humano (front interno, painel) — aí a pergunta deixa de
  ser "quem é o processo" e passa a ser "quem é a pessoa", e a resposta é OIDC;
- a API sair do laço local sem TLS na frente — a decisão vira dívida no mesmo
  instante;
- houver necessidade de escopo por consumidor (o MCP só lê; o worker escreve) —
  hoje uma chave vale para toda a superfície.
