# ADR-0001 — Camada de aplicação explícita

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

A regra de negócio do caso de uso "solicitar pesquisa" vivia dentro do lambda de
`POST /accounts/{id}/research`: suppression, cooldown mensal, rejeição de run
concorrente, validação de transição e a composição transacional
`research_run + status + outbox`. Cerca de 90 linhas de política dentro de um
adaptador HTTP.

O handler do worker tinha o mesmo problema, em menor grau: `ResearchRequestedHandler`
era um caso de uso vestido de handler, acoplado ao formato de evento do outbox.

Consequências práticas:

- testar "conta suprimida não entra em pesquisa" exigia subir Postgres em
  container e `WebApplicationFactory`;
- o pipeline de ingestão, que precisa criar conta sem passar por HTTP, não tinha
  onde reusar a regra;
- as portas (`IAccountRepository` e companhia) eram declaradas **dentro** de
  `Infrastructure`, então qualquer consumidor precisava referenciar o assembly do
  Npgsql para enxergar o contrato.

## Opções consideradas

1. **Manter como está.** Barato hoje. Cada novo agente (Auditor, Matcher, People
   Finder, SDR) repetiria a forma, e a regra continuaria só testável com
   infraestrutura real.
2. **Extrair serviços de aplicação dentro de `Infrastructure`.** Resolveria a
   testabilidade parcialmente e manteria a inversão de dependência errada.
3. **Projeto `AutoHous.Revenue.Application` com portas e casos de uso.**

## Decisão

Opção 3. Direção de dependência:

```
Api ----------> Application -------> Domain
Worker -------> Application -------> Domain
Ingestor -----> Application -------> Domain
Infrastructure -> Application ------> Domain
Agents -------> Application -------> Domain
```

Portas vivem na Application; implementações, em `Infrastructure` e `Agents`.
A composição concreta acontece nos pontos de entrada.

## Consequências

**Positivas**

- Regra de negócio testável sem banco: `AutoHous.Revenue.Application.Tests` roda
  em ~350 ms e não referencia Npgsql, Testcontainers nem HttpClient.
- O mesmo caso de uso serve API, worker e CLI de ingestão.
- Endpoints ficaram finos: validam formato, chamam o caso de uso, traduzem o
  resultado em status HTTP.

**Negativas**

- Um projeto e uma camada de indireção a mais.
- Resultados de caso de uso precisam de um tipo próprio (`RequestResearchResult`)
  para que o adaptador escolha o status certo sem inspecionar exceção.

**Riscos**

- Casos de uso anêmicos, que só encaminham para o repositório. Mitigado por
  `UseCaseShapeTests`, que exige resultado explícito e dependência só de portas.

## Gatilho de revisão

Se, em três meses, mais da metade dos casos de uso for encaminhamento de uma
chamada só, a camada não está pagando o custo e deve ser reavaliada.
