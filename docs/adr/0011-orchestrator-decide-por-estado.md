# ADR-0011 — O Orchestrator decide por estado; o dispatcher só roteia comando

**Data:** 2026-08-31 · **Status:** Aceito

## Contexto

O `OutboxDispatcher` roteava todos os eventos por `event_type` num `switch`.
Entre os casos havia dois tipos de coisa muito diferentes:

```csharp
case EventTypes.AuditRequested:      // "audit.requested vai para o auditor"
case EventTypes.ResearchCompleted:   // "pesquisa concluida significa pontuar"
```

O primeiro é infraestrutura: não depende do estado da conta e não é decisão de
negócio. O segundo é política — e estava escrito dentro de um adaptador.

A análise de lacunas já tinha nomeado o problema: *"o roteador é infraestrutura, o
orquestrador é política. Quando o segundo existir, ele é um caso de uso — não um
`switch` maior no dispatcher."*

Havia um agravante estrutural. O `switch` só enxergava o evento que acabara de
chegar, então **não havia de onde perguntar "esta conta já tem auditoria?"**. A
cadeia era fixa por construção: para acrescentar o Product Matcher, o caminho de
menor resistência seria fazer o consumidor de `score.ready` chamá-lo, e o de
`products.matched` chamar o People Finder — espalhando a ordem das etapas por
cinco casos de uso, cada um sabendo só do seu vizinho.

## Opções consideradas

1. **Encadear os casos de uso.** Cada etapa enfileira a seguinte no final da sua
   transação. Simples, e é o que o código já estava fazendo com
   `research.completed → score`.
2. **Uma máquina de estados por evento no dispatcher.** Um `switch` maior, com o
   estado da conta lido em cada caso.
3. **Separar comando de conclusão.** Comandos continuam roteados por tipo;
   **todas** as conclusões vão para um consumidor único que lê o estado da conta e
   emite o comando seguinte.

## Decisão

Opção 3. `AccountOrchestration.Decide` é função pura no domínio:
`AccountProgress` entra, `OrchestrationDecision` sai.
`DecideNextActionUseCase` lê o retrato, chama a decisão e escreve o efeito —
comando enfileirado, run criado quando o comando precisa de um, transição quando
cabe, e baixa do evento de entrada, tudo numa transação.

A opção 1 foi descartada porque distribui a ordem do funil pelos casos de uso, e
uma conta que chega pelo meio — importada já com pesquisa, ou reprocessada depois
de um sinal novo — não teria como retomar no ponto certo. A opção 2 mantém a
política dentro da infraestrutura, que é o antipadrão que motivou o ADR.

## Consequências

**Positivas**

- A ordem das etapas está em **um lugar só**, e é uma função pura testável sem
  banco, sem fila e sem Hermes.
- Uma conta retoma no ponto certo, venha de onde vier.
- Acrescentar uma etapa é acrescentar uma regra à função, e não reescrever o
  `switch` de dois adaptadores.
- Toda decisão carrega justificativa. É o único rastro de por que uma conta parou
  onde parou — sem ela, "esta conta não anda" não tem investigação possível.

**Negativas**

- **Um salto a mais na fila.** `research.completed` não vira score diretamente:
  vira `score.requested`, que vira score. O custo é uma linha no outbox por etapa,
  contra a alternativa de ter a política espalhada.
- **Os testes de cadeia não podem contar saltos.** O comprimento passou a depender
  do estado da conta — uma conta com domínio passa pela auditoria antes de
  pontuar. `TestData.DrainUntilAsync` drena até uma condição; contar saltos fixaria
  a forma da cadeia, que é exatamente o que este ADR existe para poder mudar.
- **Uma leitura nova por conclusão.** `v_account_progress` reúne dez subconsultas
  sobre sete tabelas.

## A leitura é uma só, e isso é correção

`v_account_progress` existe em vez de seis chamadas a repositórios já existentes
por uma razão que não é desempenho: a decisão é função do retrato **inteiro**, e
seis leituras independentes veem seis instantes diferentes. Uma auditoria
concluindo entre a terceira e a quarta faria o Orchestrator decidir sobre um
estado que nunca existiu.

## As guardas contra laço

São a parte que mais custaria se faltasse, porque um orquestrador que gira não
produz erro visível — produz uma fila que consome modelo até alguém notar a
fatura.

| Guarda | O que impede |
|---|---|
| `HasRunInFlight` | dois eventos da mesma rajada pedirem duas auditorias |
| `LastAuditedAt` marca a auditoria **tentada** | domínio morto pedir auditoria para sempre |
| `ContactsSearchedAt` responde "já procuramos?" | busca vazia ser refeita a cada evento |
| `NextResearchAt` empurrado pelo persister | retrato vencido pedir pesquisa em laço |
| Chave de idempotência ancorada na safra | fit e contatos serem refeitos sobre os mesmos fatos |

As chaves não seguem todas o mesmo formato, e a diferença diz o que significa "de
novo" para cada etapa. Fit e contatos ancoram na **safra** que os originou —
refazer sobre os mesmos fatos não produziria nada novo. Score e pesquisa ancoram
no **tempo**, porque os dois são legitimamente repetíveis quando um fato novo
chega.

## O que o Orchestrator deliberadamente não faz

**Não suprime.** Um desqualificador `high` — recuperação judicial, encerramento —
manda a conta para `nurture` e para revisão humana. Suppression é decisão de gente
(Regra 2), e um agente que conclui "esta empresa encerrou atividade" a partir de
uma página desatualizada não pode banir a conta sozinho.

**Não consome `account.created`.** Nenhum produtor o emite hoje; quem passaria a
emiti-lo é o pipeline de ingestão, que cria contas às centenas de milhares. Ligá-lo
faria uma carga nacional da Receita pedir pesquisa para cada linha — decisão de
orçamento, não de arquitetura. Entrar no funil continua sendo ato explícito.

## Gatilho de revisão

Quando existir o SDR (A06) e a cadeia passar a ter ramos que dependem de resposta
do prospect — e não só do estado interno da conta —, a decisão deixa de ser uma
função do retrato e passa a envolver a máquina de estados de `opportunities`. Aí
são duas máquinas acopladas por evento, e vale reavaliar se continuam sendo uma
função só.
