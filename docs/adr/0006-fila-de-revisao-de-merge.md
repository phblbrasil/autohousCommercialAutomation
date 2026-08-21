# ADR-0006 — Fila de revisão em vez de merge otimista

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

A resolução de grupo econômico decide se um CNPJ que chega abre conta nova ou
entra numa existente. Nomes de empresa automotiva são genéricos e repetitivos:
"Auto Center", "Vento Sul Veículos", "Center Car". A similaridade de trigrama
produz uma faixa cinzenta grande.

Os dois erros possíveis não custam a mesma coisa:

- **falso split** — duas contas do mesmo grupo. Custa pesquisa paga duplicada e
  dois SDRs no mesmo grupo. Corrigir é unir, e a evidência sobrevive.
- **falso merge** — dois grupos distintos na mesma conta. Uma tese comercial
  errada num deles, evidência de um atribuída ao outro, e a mensagem "vi que
  vocês têm 12 lojas" mandada para quem tem 3. Corrigir exige separar
  evidências, sinais, contatos e histórico — reconstrução manual.

## Opções consideradas

1. **Merge otimista acima de um limiar único**, corrigindo depois. Cobertura
   automática máxima, e o erro mais caro acontece calado.
2. **Nunca unir automaticamente.** Seguro e inviável: 300 contas no piloto viram
   300 decisões humanas.
3. **Três faixas: automático, revisão, conta nova.**

## Decisão

Opção 3, com os limiares do §11 vivendo no domínio (`AccountSimilarity`):

| Condição | Ação |
|---|---|
| mesma raiz de CNPJ | anexa, confiança 1.00 |
| similaridade ≥ 0.90 **e** mesma UF | anexa |
| similaridade ≥ 0.75 | fila de revisão |
| abaixo disso | conta nova, confiança 1.00 |

A exigência de mesma UF na regra automática não é arbitrária: grupos automotivos
são regionais, e "Vento Sul Veículos" em SP e em RS são, quase sempre, empresas
diferentes.

A linha em revisão fica em `review` e **não vira conta**. Deixar a conta nascer e
"consertar depois" é o caminho para duas contas do mesmo grupo receberem pesquisa
paga em paralelo — exatamente o custo que a fila evita.

## Consequências

**Positivas**

- O erro caro exige uma pessoa dizer sim.
- A raiz de CNPJ resolve a maior parte dos casos reais sem julgamento nenhum, o
  que mantém a fila pequena.
- `AccountGroupResolver` é puro e testável com uma lista em memória.

**Negativas**

- A fila precisa de dono. Sem revisão, as linhas ficam paradas e as contas nunca
  entram no funil.
- A CLI sai com código 3 quando a resolução automática fica abaixo de 85%.

**Rejeitar não é descartar**

Se o revisor diz que a empresa não pertence ao grupo sugerido, ela vira conta
própria. Sem isso, a linha revisada e negada sumiria do funil depois de ter
custado revisão humana — o pior desfecho possível.

## Gatilho de revisão

Se a fila passar de ~10% do lote de forma consistente, os limiares estão errados
para o mercado automotivo brasileiro e devem ser recalibrados contra as decisões
já tomadas — que ficam registradas em `account_merge_candidates` com
`decided_by`, e são exatamente o conjunto de treino para isso.
