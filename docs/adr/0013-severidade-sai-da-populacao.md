# ADR-0013 — A severidade de um achado sai da população, não da opinião

**Data:** 2026-09-02 · **Status:** Aceito

## Contexto

O [ADR-0012](0012-fit-por-perfil-de-operacao.md) decidiu que o peso de cada
critério varia com o perfil da operação. Ao ir implementar, o desenho esbarrou
num problema de escala que não é técnico:

**5 produtos × ~6 critérios × 6 perfis ≈ 180 constantes.** Todas inventadas.
Ninguém consegue defender por que `canais_externos` vale 25 e não 22 para a H4, e
ninguém vai manter uma tabela desse tamanho. Uma planilha de números arbitrários
com aparência de precisão é pior que um número redondo assumido como chute:
ela convida a discutir o valor errado.

E há um problema anterior. A auditoria profunda ([0018](../../database/migrations/0018_auditoria_aeo_geo.sql))
acrescentou 19 medidas — robôs de IA bloqueados, tipos de JSON-LD, texto sem
JavaScript. Quanto vale bloquear `OAI-SearchBot`? Não há resposta possível por
introspecção. Depende de quantas lojas do mesmo porte fazem o mesmo.

## O que a base permitia, e não parecia

A primeira ideia foi calibrar sondando a população. Morreu na primeira consulta:
**nenhuma das 38.332 contas do ICP tem domínio.** A Receita não traz site; quem
descobre é o Researcher, uma chamada de modelo por conta.

Mas a Receita traz `email`, e em empresa deste porte o domínio do e-mail **é** o
site:

| | estabelecimentos | com e-mail | domínio próprio | e-mail pessoal |
|---|---|---|---|---|
| Concessionária | 14.829 | 12.813 | **7.423** (58%) | 5.390 |
| Revenda | 31.877 | 28.705 | **6.884** (24%) | 21.821 |

**14.307 domínios candidatos, de graça.** A sonda é HTTP puro: custo de modelo
zero. A calibração é possível — só não pelo caminho que parecia.

E o próprio provedor já é medida: 76% das revendas operam em e-mail pessoal
contra 42% das concessionárias. A segmentação que o ADR-0012 propõe **já aparece
no dado**, antes de qualquer agente rodar.

## Decisão

### 1. O peso se decompõe em três perguntas

O que hoje é um número passa a ser três coisas com origens diferentes:

| | responde | de onde vem |
|---|---|---|
| **Aplicabilidade** | isso significa algo para esta operação? | binária, da natureza (CNAE) |
| **Severidade** | quão ruim é este achado? | **medida** — percentil do segmento |
| **Alavancagem** | quanto move o negócio *deste* perfil? | modificador nomeado — julgamento |

Só a terceira é opinião, e ela cabe em ~15 modificadores em vez de 180 células.

### 2. A severidade sai do percentil do segmento

Um achado presente em 90% do segmento **não é argumento comercial** — é o normal
do mercado. Presente em 5%, é diferenciador.

Isso troca constante inventada por distribuição medida, e traz um efeito de
graça: **o peso passa a variar por perfil sozinho**, porque a distribuição difere
por perfil. Não é preciso tabela por perfil para que concessionária média e
revenda pequena tenham pesos diferentes — a população faz isso.

Consequência prática para quem vende: a nota vira uma frase que o SDR usa. *"Seu
site carrega pior que 80% das concessionárias do seu porte"* é mais defensável e
mais útil que *"seu site tirou 43"*.

### 3. Achado catastrófico é bandeira, não ponto

`noindex` ativo, site fora do ar, busca de IA bloqueada: a perda é assimétrica.
Diluir isso numa média ponderada produz nota mediana, e conta com nota mediana
ninguém liga. Estes achados **não entram na soma** — marcam a conta.

### 4. A alavancagem também deve sair de observável quando der

Exemplo que motivou a regra: quanto pesa a invisibilidade para motor de IA?

A resposta não é "mais para conta grande". É **mais para quem depende de canal
próprio**. Uma loja cujo estoque vive no Webmotors tem o marketplace como camada
de descoberta e sofre menos; uma que investe em tráfego próprio sofre direto. E
"investe em canal próprio" é medido pela sonda — pixel de anúncio, analytics.

Sempre que a alavancagem puder ser ancorada num fato medido, ela deixa de ser
modificador de julgamento.

### 5. Prior é rotulado como prior

O que sobrar de julgamento entra marcado no código, com a telemetria que o
aposenta definida junto: contactado → respondeu → reunião, por perfil. Enquanto
não houver essa cadeia, os pesos são apostas — e dizer isso é o que permite
corrigi-las.

## Como se calibra

Comando de calibração no Ingestor, sem custo de modelo:

1. extrai o domínio candidato de `companies_cnpj.email`, descartando provedor
   pessoal (a lista já existe em `ContactPolicy.PersonalEmailProviders`);
2. sonda uma amostra estratificada por natureza × porte;
3. grava a medição crua;
4. a distribuição por segmento sai de uma view.

Responde por medição perguntas que hoje são achismo: *quantas concessionárias
brasileiras bloqueiam busca de IA? quantas têm `Vehicle` em JSON-LD? qual o TTFB
mediano do setor?*

**Sobre sair para a internet:** é um GET da home mais `robots.txt`, `sitemap.xml`
e `llms.txt` — quatro requisições por domínio, uma vez. A sonda se identifica
como `AutoHousRevenueBot/1.0` no User-Agent. A concorrência é limitada e a
amostra é explícita, e não a base inteira por padrão. Uma ferramenta que mede se
o site bloqueia robô mal-educado não pode ser um.

## Consequências

**A favor.** O número de constantes inventadas cai de ~180 para ~15. Cada peso
passa a ter origem declarada. A calibração custa zero de modelo. E a saída vira
argumento comercial em vez de nota abstrata.

**Contra.** A nota passa a depender da amostra: uma amostra enviesada enviesa
todo mundo. Mitigação — a amostra é estratificada, o tamanho por estrato fica
registrado junto da distribuição, e segmento com amostra pequena reporta baixa
confiança em vez de fingir precisão.

**O que isto NÃO resolve.** Percentil mede o que é raro, não o que converte. Um
achado raro pode ser irrelevante comercialmente. Só a telemetria de conversão
separa os dois, e ela não existe ainda — por isso o item 5.

**Custo de recalibrar.** A distribuição envelhece: o mercado inteiro adota
`Vehicle` em JSON-LD e o que era diferenciador vira base. A severidade precisa
ser recalculada periodicamente, e a versão da calibração entra junto do score —
sem isso, "por que esta conta caiu de 82 para 68?" ganha uma causa invisível.

## Gatilho de revisão

Reabrir quando: (a) houver telemetria de conversão suficiente para calibrar
contra resultado em vez de raridade — aí o percentil vira ponto de partida, não
resposta; (b) a taxa de acerto do domínio extraído do e-mail se mostrar baixa na
prática, o que tornaria a amostra não representativa; (c) o headless browser
entrar, porque metade das medidas muda de valor quando o JavaScript roda.
