# ADR-0012 — O fit pesa por perfil de operação, e o perfil sai da Receita

**Data:** 2026-09-02 · **Status:** 🟡 **Proposta** — precisa de decisão antes de virar código

## Contexto

O `ProductFitScoring` aplica **os mesmos pesos a todas as contas**. Um critério
`unidades` que paga 30 pontos por ter 10+ lojas é o mesmo para a revenda de
esquina e para o grupo com 18 filiais.

A observação que abriu esta discussão foi comercial: *cada cliente, a depender do
tamanho e da natureza da operação, terá dores diferentes*. Uma revenda pequena
sente a ausência de site de qualidade e de ambiente integrado, e **preço é fator
decisivo**. Uma concessionária de médio/grande porte sente a qualidade do site
pela economia da campanha — tempo de carregamento, gatilhos e CTAs afetam CPA e
CPL, e portanto a geração de lead — e sente a **falta de integração muito mais**.

Ao procurar como implementar isso, apareceu um fato que reenquadra o problema.

---

## 1. O que a base diz, e que ninguém está usando

A carga da Receita já classificou 712.904 estabelecimentos. Distribuição real:

| `porte` | linhas | % | MEI | capital mediano | filiais |
|---|---|---|---|---|---|
| **01** micro | 639.852 | 89,8% | 394.004 | R$ 6.500 | 6.404 |
| **03** EPP | 41.028 | 5,8% | 0 | R$ 90.000 | 3.540 |
| **05** demais | 32.021 | 4,5% | 1 | R$ 1.610.000 | 18.451 |

E por família de CNAE:

| | micro | EPP | demais | total |
|---|---|---|---|---|
| **4511** veículos (novos/usados) | 27.658 | 7.574 | 11.474 | **46.706** |
| **4520** oficina | 435.515 | 8.564 | 1.681 | **445.760** |
| **453** autopeças | 150.259 | 18.288 | 9.527 | **178.074** |
| **454** motocicletas | 6.713 | 1.853 | 3.475 | **12.041** |

Estrutura de grupo, por conta: **661.238 têm um único CNPJ**; 11.869 têm dois;
3.351 têm três ou quatro; 1.142 têm cinco a nove; 399 têm dez ou mais.

**Duas leituras saltam daqui.**

**A base não é de revendas — é de oficinas.** 445 mil oficinas contra 46 mil
vendedores de veículo. O catálogo inteiro (vitrine de estoque, distribuição de
estoque entre canais) fala com quem **vende carro**. Para uma oficina, os
critérios `vitrine`, `volume_de_estoque` e `canais_externos` não são "dor baixa":
são **pergunta sem sentido**. Hoje eles pontuam zero e a conta parece fria, quando
na verdade não foi diagnosticada.

**89,8% da base tem um CNPJ e capital mediano de R$ 6.500.** O critério
`grupo_economico` e o `unidades` — 45 dos 100 pontos do MotorHub — são
estruturalmente inalcançáveis para nove em cada dez contas.

---

## 2. O que está errado hoje, concretamente

1. **O porte vem do agente, não da Receita.** `StoreCount` e `InventoryEstimate`
   são autodeclarados pelo Researcher e vêm nulos com frequência. Enquanto isso
   `porte`, `capital_social`, `opcao_mei`, `opcao_simples`, `matriz_filial`,
   `data_abertura` e `cnae_principal` estão no banco para **100% da base**, vindos
   da fonte oficial. O `ProductFitRepository` lê `companies_cnpj` só para
   `count(*)`.

2. **Critério pago por ausência.** `canal_de_conversa` dava 30 pontos por *não*
   detectar chat; `medicao`, 15 por *não* detectar analytics. A sonda só enxerga
   assinatura que conhece — um botão de WhatsApp caseiro não conta. O próprio
   arquivo admite que "ausência erra por ignorância", e mesmo assim deixa a
   ausência decidir a porta de entrada. Foi o que fez o AutoTalk vencer.

3. **Não existe dimensão de preço.** Para 394 mil contas MEI, a acessibilidade é
   a variável que decide a conversa — e ela não entra na nota. O fit recomenda
   BoxTech ("plataforma para operações maiores") sem nada que impeça.

4. **O FrontCar só pontua quando o site é ruim.** Num site razoável ele
   desaparece — ficou em último no ensaio, 45,41. Se o site é a porta natural da
   conversa, medir só a dor não sustenta essa abertura.

5. **A cobertura não distingue "não observado" de "não se aplica".** Uma oficina
   com `vitrine` nula aparece como diagnóstico incompleto, e não como critério
   inaplicável. A fila não consegue separar "falta pesquisar" de "não é para essa
   conta".

---

## 3. Os eixos propostos

Nenhum sai do agente. Todos são determinísticos, derivados de fato já persistido —
o que mantém o ADR-0005 de pé: a nota continua reproduzível e explicável.

| Eixo | De onde sai | Por que importa |
|---|---|---|
| **Natureza** | `cnae_principal` → `AutomotiveOperation` | decide **quais critérios se aplicam**, antes de qualquer peso |
| **Porte** | `porte`, `capital_social`, `opcao_mei/simples`, nº de CNPJs, `matriz_filial` | decide **o peso** de cada critério aplicável |
| **Capacidade de investir** | `porte` + `capital_social` + regime tributário | decide **qual produto cabe no bolso** |
| **Maturidade digital** | `technologies` (ads, analytics, CRM, DMS) | separa "não tem" de "tem e está ruim" |
| **Estrutura física** | filiais, municípios e UFs distintas | dor de coordenação é diferente de dor de volume |
| **Densidade do mercado** | `rf_municipio_stats`, `rf_cnae_stats` | concorrência local muda a urgência do site |

---

## 4. As hipóteses de dor, por perfil

Aqui está o miolo. Cada hipótese diz **o que prevemos**, **o que a torna falsa** e
**que produto ela sustenta**. São hipóteses: entram no código como pesos, e saem
se a taxa de conversão não confirmar.

### H1 — Micro vendedor de veículo (porte 01, 1 CNPJ, MEI/Simples)
> ~27,6 mil contas

**Dor prevista:** não tem vitrine própria utilizável; o estoque vive no
Webmotors/OLX e no Instagram. Republica o mesmo carro à mão em cada canal.
**Preço é a objeção antes de qualquer argumento técnico.**

**Sustenta:** FrontCar em nível de entrada. **Nunca BoxTech** — capital mediano de
R$ 6.500 não compra plataforma.
**Refuta se:** a conta já tem site com vitrine e tracking funcionando.
**Peso proposto:** `vitrine` e `achabilidade` sobem; `unidades`, `grupo_economico`
e `integracao_complexa` **saem da conta** (não valem zero — não se aplicam).

### H2 — Revenda estabelecida (porte 03, 1–2 CNPJs)
> ~7,5 mil contas

**Dor prevista:** tem site, tem volume, e começa a perder lead por falta de
processo. O estoque em 2–3 canais já dá retrabalho, mas ainda é administrável.
**Sustenta:** FrontCar (qualidade) e AutoFollow (CRM) — nesta ordem.
**Refuta se:** já opera CRM detectável e o lead não vaza.
**Peso proposto:** `captura_sem_destino` e `conversao` sobem; `unidades` continua
baixo.

### H3 — Concessionária de marca, médio/grande (porte 05, 4511, filiais)
> ~11,5 mil contas

**Dor prevista:** a qualidade do site é **custo de mídia**. Carregamento lento e
CTA fraco elevam CPA/CPL, e isso aparece no orçamento de campanha antes de
aparecer em qualquer outro lugar. E a **falta de integração é a dor maior**: DMS
da marca, sistema da fábrica, múltiplos portais, múltiplas unidades — cada
alteração feita N vezes.
**Sustenta:** MotorHub como entrada; FrontCar com argumento **econômico** (CPA/CPL),
não estético; BoxTech como consolidação.
**Refuta se:** já tem integrador e o estoque bate entre canais.
**Peso proposto:** `canais_externos`, `heterogeneidade` e `dms` sobem muito;
`desempenho` deixa de ser nota de site e passa a ser **argumento de custo**.

### H4 — Grupo multimarca com 3+ CNPJs
> ~4,9 mil contas

**Dor prevista:** a dor não é volume, é **coerência**. Preço divergente entre
unidades, estoque publicado em datas diferentes, marca inconsistente.
**Sustenta:** MotorHub e BoxTech.
**Refuta se:** as unidades operam de forma independente por decisão, e não por
limitação.
**Peso proposto:** nº de CNPJs e dispersão geográfica passam a valer de verdade.

### H5 — Oficina e autopeças (4520, 453)
> ~623 mil contas — **o grosso da base**

**Dor prevista:** não é distribuição de estoque. É **ser encontrada** (busca
local, Google Meu Negócio) e **agendar** — que é conversão de outro tipo.
**Sustenta:** FrontCar em recorte de presença local. **Nenhum dos outros quatro.**
**Refuta se:** o volume de serviço não depende de canal digital.
**Peso proposto:** os critérios de estoque **não se aplicam**; entram
achabilidade local e densidade de concorrência do município.

> **H5 é a que tem mais consequência e menos evidência.** Se ela se confirmar,
> 92% da base precisa de um recorte de produto que hoje não existe. Se não se
> confirmar, essas 623 mil contas deveriam sair do ICP — e a fila fica honesta.

### H6 — Empresa nova (`data_abertura` < 24 meses)
**Dor prevista:** ainda não tem site nem processo; compra o básico e tem urgência.
**Sustenta:** FrontCar.
**Refuta se:** abertura recente for só reorganização societária de operação antiga.

---

## 5. Mecanismo proposto

1. **`OperationProfile`**, record puro no domínio, derivado por função
   determinística dos fatos da Receita. Nada de agente.
2. **Aplicabilidade antes de peso.** Cada critério declara para quais naturezas
   ele vale. Inaplicável **sai do denominador da cobertura** — é a correção do
   defeito 5.
3. **Tabela de pesos por perfil**, versionada em `ProductFitScoring.Version`. É o
   que mantém "por que esta conta caiu de 82 para 68?" respondível: a resposta
   passa a incluir "porque o perfil dela mudou de X para Y".
4. **Teto de produto por capacidade de investimento** — a peça que responde ao
   "preço é decisivo". Um produto acima da faixa da conta não abre conversa.

---

## 6. Perguntas abertas — preciso de decisão

1. **H5 (oficina/autopeças) entra ou sai?** São 623 mil contas, 92% da base. Se
   entram, falta produto; se saem, o funil encolhe para ~50 mil e fica honesto.
   **É a decisão de maior impacto deste documento.**
2. **Qual o teto de produto por faixa?** Preciso da regra "MEI não recebe X;
   porte 03 não recebe Y" — ou do preço de cada produto para eu derivá-la.
3. **O argumento do FrontCar muda por porte?** Em H3 ele é CPA/CPL; em H1 é "não
   ter vitrine". Se sim, o prompt do A04 precisa receber o perfil.
4. **`data_abertura` e densidade de município entram na v1 ou ficam para depois?**

---

## Consequências

**A favor:** a nota passa a refletir a operação real; 92% da base deixa de ser
diagnosticada com critérios que não se aplicam; o porte deixa de depender do que
o agente autodeclarou; preço entra na decisão.

**Contra:** mais superfície para explicar. Hoje há uma tabela de pesos; passa a
haver uma por perfil, e "por que mudou?" ganha uma causa a mais. Mitigado pelo
versionamento e por gravar o perfil junto do fit.

**Não decidido aqui:** os valores dos pesos. Este ADR decide a *forma*; os números
saem depois, e mudam sem novo ADR.

---

## Gatilho de revisão

Reabrir quando: (a) o AutoTalk ficar pronto e voltar à disputa; (b) houver
conversão real de 30+ contas por perfil para calibrar contra resultado em vez de
hipótese; (c) o catálogo ganhar produto para serviço (H5); (d) a base deixar de
ser dominada por oficinas — hoje 445 mil de 712 mil.
