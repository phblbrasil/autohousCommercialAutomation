# ADR-0005 — Scoring determinístico, sem LLM

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

O Opportunity Score prioriza a fila de execução comercial. O frame 06 dos dois
boards define pesos (30/30/25/15) e diz, explicitamente, que o score deve ser
explicável e recalculado quando chega sinal novo.

O sistema já tem uma camada de agentes funcionando. Seria natural pedir ao modelo
que "avalie esta conta de 0 a 100".

## Opções consideradas

1. **LLM produz o score.** Absorve nuance que uma tabela de pesos não captura.
   Custa uma chamada paga por recálculo, não é reproduzível, e "por que caiu de
   82 para 68?" não tem resposta auditável.
2. **LLM produz o score e uma justificativa.** A justificativa é gerada *depois*
   do número — é racionalização, não explicação.
3. **Aritmética determinística sobre fatos persistidos.**

## Decisão

Opção 3. `OpportunityScoring.Calculate` é função pura: `ScoringInputs` entra,
`OpportunityScore` com breakdown sai.

O papel do modelo é produzir os **fatos** — auditoria de site, sinais com data e
fonte, contatos com confiança. Nunca a aritmética.

## Consequências

**Positivas**

- Recálculo custa zero e roda em milissegundos, então pode acontecer a cada sinal
  novo.
- `account_scores` é append-only e `feature_snapshot` guarda o breakdown: a
  variação entre duas safras é diffável.
- Ajustar peso é editar uma tabela, não reescrever prompt e revalidar saída.

**Negativas**

- Pesos são hipótese inicial, não modelo aprendido. O board já assume isso
  ("pesos editáveis").
- Nuance que não está nos fatos não entra no score.

**Mitigação da segunda**

`OpportunityScore.Coverage` reporta a fração dos pontos que veio de fato
observado. Score baixo com cobertura baixa é pedido de mais pesquisa, não
veredito — e `ScoreComponent.Observed` distingue "olhamos e não tem" de "ainda
não sabemos".

## Gatilho de revisão

Quando houver ~100 oportunidades fechadas (ganhas e perdidas), comparar o score
com o desfecho real. Se os pesos não separarem ganho de perda, o caminho é
regressão sobre o histórico — ainda determinística e ainda explicável —, não um
LLM opinando.
