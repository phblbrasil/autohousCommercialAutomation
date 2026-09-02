# Camada de agentes

## Dois fatos da documentação oficial do Hermes que moldam tudo

### 1. Não existe endereçamento de agente por nome

`POST /v1/runs` não recebe "qual agente executar". `delegate_task` é dinâmico,
decidido pelo modelo em runtime, e a documentação não descreve como definir tipos de
agente customizados.

**Consequência:** os seis agentes do §17 do blueprint são conceitos **da aplicação**.
O worker escolhe prompt + skill + schema por tipo de run. `agent_runs.agent_name` é
rótulo nosso, útil para observabilidade e custo, não uma entidade do Hermes.

Isso é o que torna a tabela abaixo possível: quatro "agentes" que, do lado do
Hermes, são a mesma chamada com prompts diferentes.

### 2. Skills não têm structured output forçado

Não há `response_format` garantido. O modelo devolve texto.

**Consequência:** `StructuredOutputValidator` não é hardening — é caminho crítico. Sem
ele, o sistema inteiro repousa sobre a esperança de que o LLM formate JSON corretamente.

O caso de uso depende da porta `IStructuredOutputValidator`, não da classe: ele
precisa da capacidade "transformar texto em `ResearchProfile` válido", não da
implementação "JSON Schema draft 2020-12 via JsonSchema.Net".

## Os quatro agentes construídos

| # | Agente | O que o modelo produz | O que a plataforma **não** delega |
|---|---|---|---|
| A02 | Researcher | retrato da empresa, com evidências | nada — é o único cujo output é integralmente do agente |
| A03 | Website Auditor | o que a página significa para quem vende carro | as sete notas, e tudo que a sonda mede — inclusive AEO e GEO (§ abaixo) |
| A04 | Product Matcher | o argumento e a objeção | o fit, a porta de entrada e **quais produtos podem ser ofertados** |
| A05 | People Finder | quem decide e por onde é alcançável | a persona (`PersonaCatalog`) e o score de contactabilidade |

A coluna da direita é a que descreve o desenho. Em três dos quatro, o modelo
produz **fatos e texto**, e a aritmética é da plataforma — pela razão do
ADR-0005: "por que o MotorHub caiu de 78 para 51?" precisa de resposta auditável.

### A sonda mede fundo, e mede coisas que o modelo não veria

O A03 tem duas metades, e a divisão é a regra central do sistema: a **sonda
mede**, o **agente observa com evidência**, a **plataforma pontua**. Um modelo de
linguagem não observa tempo de resposta nem peso de página — ele estima, e
estimativa travestida de medição vira Technology Pain e sai numa abordagem
comercial como se fosse fato.

A sonda usa um parser de HTML de verdade (AngleSharp), e não regex. A troca
aconteceu quando o tipo de pergunta mudou: contar imagem **com** atributo,
extrair texto visível sem script nem estilo, separar link interno de externo —
nada disso se resolve com casamento de padrão sobre texto. O motor antigo também
errava em silêncio: `<h1>` dentro de comentário HTML contava como título.

Três grupos de medida que o auditor produz hoje:

| | pergunta que responde |
|---|---|
| **GEO** | o motor generativo consegue **ler** este site? Rastreadores de IA bloqueados no `robots.txt`, `llms.txt`, indexabilidade, e texto visível sem JavaScript |
| **AEO** | o motor consegue **entender** o que está à venda? Tipos de JSON-LD (`Vehicle`, `Offer`, `AutoDealer`), NAP, hierarquia de títulos |
| **Qualidade** | o que de fato ranqueia: comprimento de título e descrição, canonical auto-referente, imagens por `alt`/dimensão/formato |

O achado mais acionável do conjunto é o bloqueio de busca de IA, e ele vem com
uma distinção que o número sozinho não tem: recusar `CCBot` é decisão legítima de
muita empresa; recusar `OAI-SearchBot` tira a loja do resultado que o comprador
vê enquanto pergunta onde achar o carro. `AiCrawlers` separa os dois.

**O que a sonda continua não vendo:** tudo que só existe depois do JavaScript
rodar. Numa vitrine em SPA isso é o estoque inteiro — e é por isso que a contagem
de veículos é pergunta para o **agente**, e nunca para a sonda.

### Produto indisponível não abre conversa

`ProductDefinition.Available` distingue "existe no catálogo" de "pode ser
oferecido hoje". Um produto indisponível **continua sendo pontuado** — a nota é o
registro de que a dor existia antes de haver o que vender —, mas não abre
conversa nem recebe argumento do agente.

Foi o caso do AutoTalk, que vencia a porta de entrada em conta grande sem estar
pronto para oferta. Em `MatchProductsUseCase.Worthwhile` o filtro vem **antes** do
corte relativo: um indisponível com nota alta puxaria o corte para cima e
derrubaria da lista os produtos que a AutoHous tem para vender.

### O Product Matcher inverte a ordem

Nos outros três, o agente vem primeiro e a plataforma valida depois. No A04 a
**plataforma decide primeiro** — qual produto serve, com quantos pontos e por
quais critérios — e o agente recebe isso pronto.

Duas consequências práticas:

1. **O agente não consegue escolher errado**, porque não escolhe. A pior falha
   que ele produz é um argumento fraco para o produto certo, e isso o ciclo de
   reparo pega.
2. **A aritmética sobrevive à falha dele.** Modelo fora do ar grava o fit assim
   mesmo: a fila continua priorizada, falta só a frase. O contrário — argumento
   sem nota — não serviria para nada, porque é a nota que ordena a fila.

O ponto 2 é o único caso, entre os quatro, em que falha de agente não perde a
etapa inteira.

### O People Finder tem uma guarda a mais

É o único contrato que carrega PII de pessoa física, e o único com duas camadas.
Além da Regra 1, vale o `ContactPolicy`:

- confiança mínima de **0,5** por contato e **0,6** por canal;
- **cada canal aponta para evidência diferente da do contato**.

A segunda é a que mais rejeita run, e a que mais importa. Achar o nome de um
diretor numa notícia e achar o e-mail dele são duas descobertas. Um
`nome.sobrenome@empresa.com.br` deduzido do padrão da casa passa em qualquer
schema, tem formato válido e aponta para uma fonte real — a notícia que citava o
nome. Só a regra de escopo o pega, e sem ela a plataforma escreveria para um
endereço que ninguém nunca viu.

Os números do prompt saem das constantes do domínio, e não escritos à mão: a
política e o prompt não podem divergir, e a única forma de garantir isso é não
escrever o número duas vezes.

## Pipeline de validação

```
texto do agente
  ↓ JsonPayloadExtractor      tolera cerca ```json, prosa, vírgula sobrando
  ↓ JSON Schema               additionalProperties: false, enums, format
  ↓ EvidenceFirstGuard        índices de evidência apontam para itens reais
  ↓ desserialização
ResearchProfile
```

Falha em qualquer etapa → **uma** tentativa de reparo devolvendo as violações ao
agente → nova falha encerra o run com `research_runs.status='failed'` e o motivo em
`error`. Nunca escrita parcial.

Uma tentativa e não várias: se o modelo não acerta com os erros em mãos, o problema é
do prompt, e insistir só queima orçamento.

## EvidenceFirstGuard

Vive no **domínio**, e não nesta camada: é a Regra 1 da governança, regra de
negócio, não detalhe de biblioteca. O schema sabe validar "`evidence_index` é um
inteiro ≥ 0"; ele não sabe quantas evidências existem. O guard fecha essa lacuna:

- todo `evidence_index` em `brands`, `locations` e `signals` aponta para item existente;
- toda evidência tem URL de fonte e confiança > 0;
- `store_count` declarado exige evidência de `claim_type` relacionado a lojas.

Sem isso, a Regra 1 do §25 seria só uma frase no documento — e uma abordagem do tipo
"vi que vocês têm 12 lojas" sairia sem lastro.

## Onde cada peça vive

| Peça | Projeto | Por quê |
|---|---|---|
| `IAgentRuntime` | Application | quem precisa da capacidade declara o contrato |
| `HermesAgentRuntime`, `FixtureAgentRuntime` | Agents | implementações |
| `IStructuredOutputValidator` | Application | o caso de uso depende da capacidade |
| `StructuredOutputValidator` | Agents | JSON Schema é detalhe de biblioteca |
| `EvidenceFirstGuard` | Domain | Regra 1 é política de negócio |
| `ProductFitScoring`, `AccountOrchestration` | Domain | funções puras; ADR-0005 e frame 05 |
| `PersonaCatalog`, `ContactPolicy` | Domain | taxonomia e PII são regra de negócio |
| `IResearchPromptBuilder` | Application | o caso de uso precisa do texto |
| `ResearchPromptBuilder` | Agents | ler prompt versionado de disco é I/O |
| `ExecuteResearchRunUseCase` | Application | nada nele é específico de rodar sob um `BackgroundService` |

O último item é o que permite testar o ciclo de reparo inteiro com portas falsas,
sem Postgres e sem Hermes.

## Runtimes

| Runtime | Quando | Custo | Determinístico |
|---|---|---|---|
| `fixture` | desenvolvimento, CI | zero | sim |
| `hermes` | ativação, produção | real | não |

O fixture lê respostas gravadas de `tests/fixtures/agent-runs/{agente}/{cenário}.json`
— cinco por agente: `success`, `malformed` (+ `-repaired`) e `missing-evidence`
(+ `-repaired`).

A distinção entre os dois casos de falha é o que cada um exercita.
`malformed` é sujeira de formato — prosa em volta, cerca de código, vírgula
sobrando — e o `JsonPayloadExtractor` resolve sozinho, sem gastar reparo.
`missing-evidence` é violação de contrato, e só o ciclo de reparo resolve. Uma
fixture `missing-evidence` que passasse no guard seria pior que fixture nenhuma:
faria o ciclo de reparo parecer coberto por teste sem que ninguém o tivesse
percorrido. `NewAgentFixtureTests` existe para impedir exatamente isso.

O cenário vem do payload do evento, o que permite exercitar sucesso, ciclo de
reparo e falha dura pelo mesmo caminho de código de produção.

`AGENT_RUNTIME` inválido falha na **inicialização**. Cair silenciosamente no fixture em
produção geraria pesquisas falsas com aparência de sucesso.

## O transporte, reconferido na fonte (31/08/2026, Hermes v0.21.0)

A seção anterior deste documento fixava o envelope de `/v1/runs` com `output` como
string, conferido em 20/08/2026. **Isso não vale mais**, e a diferença não é
cosmética: ela invalidava o transporte inteiro.

Em `~/.hermes/hermes-agent/gateway/platforms/api_server_runs.py`, `_handle_get_run`
devolve exatamente o dicionário de `_run_statuses`, tal como `_set_run_status` o
montou. E **nenhuma** chamada a `_set_run_status` passa `output` — os únicos
`env["output"]` do gateway pertencem a `/v1/responses`, outro endpoint.

```jsonc
// GET /v1/runs/{id} — terminal, sucesso, no v0.21.0
{"object": "hermes.run", "run_id": "...", "status": "completed",
 "session_id": "...", "last_event": "run.completed",
 "usage": {...}, "created_at": 0, "updated_at": 0}
//  ↑ nenhum campo com o texto final
```

O texto final existe só no evento `assistant.completed` da SSE
(`GET /v1/runs/{id}/events`), e essa fila **não guarda histórico**: quem faz polling
até ver `status: completed` chega depois de o texto ter passado. Não é um campo em
outro lugar — é um campo que não sobrevive ao polling.

**Por que isso é pior que um bug comum.** O sintoma seria `RawText` vazio em 100%
dos runs reais, reprovado pelo validador como `contract_violation`. A leitura óbvia
desse erro é *"o modelo não consegue formatar JSON"*, e a investigação começaria
pelo prompt — não pelo cliente HTTP. Em `AGENT_RUNTIME=fixture` nada disso aparece,
porque o fixture nunca passa pelo transporte.

**A correção.** `HermesOptions.Transport` passa a ser `Chat` por padrão.
`POST /v1/chat/completions` devolve o envelope OpenAI exato — `choices[0].message.content`
e `usage.prompt_tokens` / `completion_tokens` — que `RunViaChatAsync` já lia
corretamente, e é também o único dos dois que lê o header `X-Hermes-Session-Id`, de
modo que a correlação com `research_run_id` passa a funcionar em vez de falhar em
silêncio.

O caminho de `/v1/runs` continua no código, e agora **falha explicitamente** quando
um run completa sem texto, dizendo qual é o remédio. Ele volta a ser o transporte
preferível — é o desenhado para sessão longa — no dia em que o gateway expuser o
texto no status.

As outras duas divergências da conferência de agosto continuam válidas:

| Presunção | Realidade | Efeito se mantida |
|---|---|---|
| sessão pelo header `X-Hermes-Session-Id` | `/v1/runs` só lê `session_id` do corpo; o header é lido por `/v1/chat/completions` | `research_run_id` não casa entre os dois lados |
| prompt de sistema concatenado ao `input` | `instructions` é anexado ao system prompt do Hermes (`conversation_loop.py`) | a instrução do pesquisador vira preâmbulo do turno do usuário |

**Lição de método, e não de versão.** O envelope de um fornecedor conferido uma vez
é verdade com prazo de validade. O que protege o sistema não é a conferência — é o
`StructuredOutputValidator` recusar o que não bate e o cliente falhar alto em vez de
devolver vazio.

## O que liga o API Server

Não é `API_SERVER_ENABLED`. O gateway carrega a plataforma quando existe uma
`API_SERVER_KEY` **utilizável** — 16 caracteres ou mais e fora da lista de
placeholders (`gateway/config.py`). Chave fraca ou ausente e o servidor simplesmente
não sobe, com o watcher de reconexão girando em erro. `scripts/hermes-setup.sh` gera
uma chave de 48 hex e a espelha no `.env` do projeto.
