# ADR-0012 — O fit pesa por perfil de operação, e o perfil sai da Receita

**Data:** 2026-09-02 · **Status:** 🟡 **Proposta** — ICP e faixas **decididos**; pesos e teto de preço em aberto (§6)

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

## 4. O ICP, decidido

**Oficina e autopeças ficam fora do fit neste momento** — 623 mil contas, 92% da
base carregada. Não é descarte: a AutoHous terá produto direcionado a elas, e
quando tiver, elas voltam com critérios próprios. Até lá, pontuá-las com
`vitrine` e `volume_de_estoque` produziria nota baixa que parece conta fria e é,
na verdade, conta não diagnosticada. Elas **continuam na base** — sair do fit não
é sair do banco.

O ICP do fit passa a ser **venda de veículos (CNAE 4511)**: 38.332 contas.

| | até 3 | 4 a 7 | 8 a 15 | 15+ | total |
|---|---|---|---|---|---|
| **Concessionária** (novos) | 9.100 | 436 | 158 | 34 | **9.728** |
| **Revenda** (usados) | 28.459 | 109 | 24 | 12 | **28.604** |

Três coisas que esta tabela ensina e que mudam o desenho:

**A faixa é discriminante para concessionária e quase inútil para revenda.** 628
concessionárias têm 4+ unidades; entre as revendas são 145. Se o peso variasse só
por unidade, 99,5% das revendas cairiam na mesma célula — a segmentação não
segmentaria nada justamente onde está o grosso do funil.

**Unidade sozinha subestima porte.** 2.402 concessionárias de "até 3 unidades" já
são `porte 05`. Uma loja única de marca premium não é operação pequena. A faixa
precisa ser **unidades × porte**, não unidades.

**A faixa mais alta tem 34 contas.** São quase certamente as de maior valor e
merecem tratamento — mas construir peso elaborado para 34 contas enquanto 28.459
compartilham uma célula é alocar esforço ao contrário.

### 4.1 De onde sai a contagem de unidades

**Não de `StoreCount`.** Aquele campo é autodeclarado pelo Researcher e vem nulo
com frequência. A contagem sai de `companies_cnpj`: uma linha por
estabelecimento, agrupada por conta pelo account graph — que é exatamente o que
"olhar para os grupos" exige, porque a faixa vale para o **grupo**, e não para o
CNPJ isolado.

Onde as duas contagens divergirem de forma material, isso **não é empate a
resolver**: é sinal de que o agrupamento falhou, e a conta pertence à fila de
revisão de merge. O `StoreCount` do agente vira corroboração, não fonte.

## 4.2 As hipóteses de dor

**Natureza decide o que se APLICA. Porte × unidades decide o PESO.** Cada
hipótese diz o que prevê, o que a refuta e o produto que sustenta. São hipóteses:
entram como pesos e saem se a conversão não confirmar.

### H1 — Revenda de usados, até 3 unidades
> 28.459 contas — **74% do ICP**

**Dor prevista:** não tem vitrine própria utilizável. O estoque vive no
Webmotors/OLX e no Instagram, e o mesmo carro é republicado à mão em cada canal.
**Preço é a objeção antes de qualquer argumento técnico.**

**Sustenta:** FrontCar em nível de entrada. **Nunca BoxTech.**
**Refuta se:** já tem site com vitrine e tracking funcionando.
**Pesos:** `vitrine` e `achabilidade` sobem; `unidades`, `grupo_economico` e
`heterogeneidade` **não se aplicam** — e não se aplicar é diferente de valer zero.

> **Correção de uma hipótese anterior:** a primeira versão desta ADR falava em
> MEI. Está errado para venda de veículos — há **3 MEIs** em todo o CNAE 4511,
> porque o MEI é legalmente vedado ao comércio de veículos. O micro vendedor aqui
> é `porte 01` no Simples, não MEI. O teto de preço continua valendo; a variável
> que o define é `porte` e `capital_social`, não o regime MEI.

### H2 — Revenda com estrutura, 4+ unidades
> 145 contas

**Dor prevista:** o volume já dá retrabalho e o lead vaza por falta de processo.
Poucas contas, mas cada uma vale várias da H1.
**Sustenta:** FrontCar e AutoFollow; MotorHub a partir de 8 unidades.
**Refuta se:** já opera CRM detectável.

### H3 — Concessionária pequena, tier 1 (até 3 unidades)
> 9.100 contas — 2.402 já são `porte 05`

**Dor prevista:** tem site — geralmente o template da fábrica — e não controla a
vitrine. Sofre exigência de marca sem ferramenta própria. Nas 2.402 de porte 05,
a operação é grande dentro de poucas paredes: volume de campanha e estoque que
não cabem no template.
**Sustenta:** FrontCar. AutoFollow quando há mídia paga detectada.
**Refuta se:** a fábrica fornece plataforma que a concessionária considera
suficiente.
**Pesos:** `unidades` continua baixo; **`porte` compensa** — é aqui que a correção
"unidades × porte" mais importa.

### H4 — Concessionária pequena, tier 2 (4 a 7 unidades)
> 436 contas — 388 são `porte 05`

**Dor prevista:** a coordenação começa a doer antes do volume. Mesmo estoque em
site próprio, portal da marca e marketplaces; preço divergente entre unidades.
**Sustenta:** MotorHub entra na conversa; FrontCar com argumento de consistência.
**Refuta se:** as unidades já operam catálogo unificado.

### H5 — Concessionária média (8 a 15 unidades)
> 158 contas — 154 são `porte 05`

**Dor prevista:** o site é **custo de mídia**. Carregamento lento e CTA fraco
elevam CPA e CPL, e isso aparece no orçamento de campanha antes de aparecer em
qualquer outro lugar. A falta de integração já é a dor maior: DMS da marca,
sistema da fábrica, N portais, N unidades — cada alteração feita N vezes.
**Sustenta:** MotorHub como entrada; FrontCar com argumento **econômico**
(CPA/CPL), não estético.
**Refuta se:** já tem integrador e o estoque bate entre canais.

### H6 — Concessionária média/grande (15+ unidades)
> 34 contas — todas `porte 05`

**Dor prevista:** a da H5 elevada, mais **governança**: marca inconsistente entre
unidades, publicação em datas diferentes, nenhuma visão consolidada.
**Sustenta:** BoxTech como consolidação; MotorHub como porta.
**Refuta se:** já roda plataforma corporativa.

### H7 — Grupo — transversal às faixas
> 773 contas com 4+ unidades

Não é faixa: é **condição que atravessa H2 a H6**. A dor do grupo não é volume, é
**coerência** — preço divergente entre unidades, estoque publicado em datas
diferentes, marca inconsistente. Multiplica quando há mais de uma marca ou mais
de um município.
**Sustenta:** MotorHub e BoxTech.
**Refuta se:** as unidades operam independentes por decisão, e não por limitação.
**Pesos:** dispersão geográfica e contagem de marcas passam a valer de verdade.

### H8 — Empresa nova (`data_abertura` < 24 meses)
Transversal. Ainda não tem site nem processo; compra o básico e tem urgência.
**Refuta se:** a abertura recente for reorganização societária de operação antiga
— e `capital_social` alto com abertura recente é exatamente esse caso.

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

1. ~~H5 (oficina/autopeças) entra ou sai?~~ **DECIDIDO: ficam fora por ora**, com
   produto direcionado a caminho. Ver §4.
2. **Qual o teto de produto por faixa?** Preciso da regra "MEI não recebe X;
   porte 03 não recebe Y" — ou do preço de cada produto para eu derivá-la.
3. **O argumento do FrontCar muda por porte?** Em H3 ele é CPA/CPL; em H1 é "não
   ter vitrine". Se sim, o prompt do A04 precisa receber o perfil.
4. **`data_abertura` e densidade de município entram na v1 ou ficam para depois?**
5. **A revenda de usados precisa de um segundo eixo.** Unidades não separa 99,5%
   delas. O candidato natural é `porte` + `capital_social`; falta decidir se basta
   ou se é preciso um sinal de operação (estoque estimado, mídia paga detectada).

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

Reabrir quando: (a) **o produto para oficina e autopeças existir** — 623 mil
contas voltam ao fit com critérios próprios; (b) o AutoTalk ficar pronto e voltar
à disputa; (c) houver
conversão real de 30+ contas por perfil para calibrar contra resultado em vez de
hipótese — o que para a H6 (34 contas) levará tempo e talvez nunca chegue a 30,
caso em que a hipótese se decide por julgamento comercial, e não por número.
