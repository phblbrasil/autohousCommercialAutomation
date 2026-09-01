# ADR-0010 — A plataforma decide o produto; o agente escreve o argumento

**Data:** 2026-08-31 · **Status:** Aceito

## Contexto

O Product Matcher (A04) responde duas perguntas que parecem uma só:

1. **Qual produto oferecer a esta conta?**
2. **Com que frase abrir a conversa?**

O sistema já tem três agentes funcionando e um padrão estabelecido: o agente
produz, o `StructuredOutputValidator` valida, o `EvidenceFirstGuard` confere o
lastro, a plataforma persiste. Seguir o padrão significaria pedir as duas coisas
ao modelo numa passada só.

O [ADR-0005](0005-scoring-deterministico.md) já decidiu que o Opportunity Score é
aritmética, e não opinião de LLM. A pergunta aqui é se o fit por produto é o mesmo
tipo de coisa.

## Opções consideradas

1. **O agente responde as duas.** Uma passada, um contrato, o padrão dos outros
   três agentes. O modelo lê o retrato da conta e devolve "MotorHub, 78, e aqui
   está o porquê".
2. **O agente responde as duas, e a plataforma valida a escolha.** Calcula o fit
   em paralelo e rejeita o run quando a escolha do modelo diverge da conta.
3. **A plataforma decide primeiro; o agente recebe o diagnóstico pronto e escreve
   o argumento.**

## Decisão

Opção 3. `ProductFitScoring.Calculate` é função pura: `ProductFitInputs` entra,
uma nota por produto e a porta de entrada saem, com o breakdown critério a
critério. O agente recebe esse quadro na mensagem e produz `angle`, `reasons`,
`objections` e `recommended_personas` — todos com `evidence_index`.

A opção 2 foi descartada por ser a pior das três: paga o modelo, calcula a nota
**e** joga fora o trabalho do modelo quando eles discordam. Se a plataforma sabe
calcular, não há motivo para perguntar antes.

## Consequências

**Positivas**

- **O agente não consegue escolher errado, porque não escolhe.** A pior falha que
  ele produz é um argumento fraco para o produto certo — e isso o ciclo de reparo
  pega. Na opção 1, a pior falha é um argumento excelente para o produto errado,
  que passa em qualquer validação e só aparece na reunião.
- **A aritmética sobrevive à falha do agente.** Modelo fora do ar grava o fit
  assim mesmo: a fila continua priorizada, falta só a frase, e ela pode ser
  escrita depois. É o único dos quatro agentes com essa propriedade, e ela existe
  porque aqui a metade valiosa não depende do modelo. O contrário — argumento sem
  nota — não serviria para nada, porque é a nota que ordena a fila.
- **"Por que o MotorHub caiu de 78 para 51?" tem resposta.** `product_fit` é
  append-only e guarda o breakdown; a diferença entre duas safras é diffável.
- **Só os produtos que valem argumento vão ao modelo.** O de entrada mais os que
  pontuaram perto dele, no máximo três. Pedir os cinco gastaria contexto
  escrevendo a defesa de um BoxTech que pontuou 12 — texto que o SDR nunca usaria,
  e que pareceria autoritativo o bastante para alguém usar mesmo assim.

**Negativas**

- **Os pesos são hipótese, não modelo aprendido.** Um caso que a tabela não captura
  — a concessionária que quer MotorHub por um motivo idiossincrático — não aparece.
- **Duas fontes de verdade sobre "qual produto".** O catálogo determina personas e
  a tabela de pesos determina o fit; as duas precisam concordar sobre o que cada
  produto resolve. Por isso `ProductCatalog` vive no domínio, e a ferramenta MCP
  passou a ler dele em vez de manter a lista própria.
- **O prompt precisa comunicar "não observado" sem ambiguidade.** Um critério com
  zero pontos e `observed: false` não significa que está bom — significa que
  ninguém olhou. O construtor de prompt escreve `[nao observado]` por extenso na
  frente da linha, porque em JSON o modelo lê `"points": 0` e conclui a coisa
  errada.

## Sobre o piso de cobertura

`EntryMinimumCoverage = 0.5` existe para impedir que um diagnóstico incompleto
escolha a porta de entrada. **Com os pesos atuais ele nunca decide nada**: a única
forma de a cobertura cair abaixo de 0,5 é faltar a auditoria, e sem ela a nota
máxima alcançável já fica abaixo do corte de 45 nos três produtos afetados.

Fica registrado no código e coberto por um teste que **falha se um
rebalanceamento o tornar ativo**. Um guard inativo que ninguém sabe estar inativo
é pior que guard nenhum; removê-lo por estar inativo hoje seria retirar a guarda
exatamente antes da mudança que a torna necessária.

## Gatilho de revisão

O mesmo do ADR-0005: com ~100 oportunidades fechadas, comparar a porta de entrada
recomendada com o produto efetivamente vendido. Se a tabela não separar acerto de
erro, o caminho é regressão sobre o histórico — ainda determinística e ainda
explicável —, não um LLM opinando.

Um segundo gatilho, específico desta decisão: se o `angle` escrito pelo agente for
sistematicamente descartado pelo SDR em favor de texto próprio, o problema está no
prompt ou no recorte de produtos, e não na divisão de trabalho.
