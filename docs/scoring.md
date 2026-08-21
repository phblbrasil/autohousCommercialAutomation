# Opportunity Score

Frame 06 dos dois boards: **30 / 30 / 25 / 15**. O score prioriza a fila de
execução; ele não substitui julgamento comercial no Tier 1.

```
research.completed
        ↓
ScoreAccountUseCase
   │  carrega fatos de accounts, companies_cnpj, account_brands,
   │  signals, website_audits, contacts, contact_channels
   │
   ├── uma transação ──────────────────────────────
   │   account_scores  (append-only, com breakdown)
   │   accounts.tier
   │   accounts.status  researched → scored
   │   events_outbox    score.ready
   │   evento de entrada marcado como processed
   └────────────────────────────────────────────────
```

Até esta entrega, `research.completed` era marcado como processado sem
consumidor: a cadeia de eventos do frame 02 parava na pesquisa.

## Determinístico, sem LLM

O score é aritmética sobre fatos já persistidos. Roda em milissegundos, custa
zero e pode ser reexecutado à vontade quando um sinal novo chega.

Um modelo de linguagem aqui tornaria impossível responder *"por que esta conta
caiu de 82 para 68?"* — e essa pergunta é o único motivo de `account_scores` ser
append-only. O papel do modelo, quando entrar, é produzir os **fatos**
(auditoria, sinais, contatos), nunca a aritmética.

## As quatro dimensões

### Company Fit — 30

| Critério | Máx | Como pontua |
|---|---|---|
| operação (CNAE) | 5 | concessionária 5 · revenda 4 · atacado 3 · motos/intermediação 2 · locadora 1 |
| lojas | 10 | ≥10 → 10 · ≥5 → 8 · ≥3 → 6 · 2 → 4 · 1 → 2 |
| estoque | 5 | ≥500 → 5 · ≥200 → 4 · ≥80 → 3 · ≥30 → 2 · >0 → 1 |
| grupo econômico | 5 | ≥5 CNPJs → 5 · ≥3 → 4 · 2 → 3 · 1 → 0 |
| marcas | 5 | concessionária autorizada → 5 · senão ≥3 marcas → 4 · 2 → 3 · 1 → 2 |

Número de lojas vale o dobro do resto porque é o preditor mais forte de dor de
presença digital: multi-loja quebra site, estoque e atendimento ao mesmo tempo.

Grupo econômico é sempre observado — a contagem de CNPJs vem do próprio account
graph, não de pesquisa externa.

### Technology Pain — 30

| Critério | Máx | Como pontua |
|---|---|---|
| performance | 10 | `(1 − score) × 10` |
| SEO | 5 | `(1 − score) × 5` |
| múltiplos portais | 5 | binário |
| múltiplas lojas | 5 | ≥5 → 5 · ≥3 → 4 · 2 → 3 |
| integração complexa | 5 | binário |

Site pior gera mais pontos: a dimensão mede **dor**, não qualidade.

"Múltiplas lojas" aparece aqui e em Company Fit — os dois boards listam o
critério nas duas dimensões, e eles medem coisas diferentes: lá é porte, aqui é a
dor operacional de manter várias vitrines coerentes.

Sem auditoria de site, 20 dos 30 pontos ficam em zero — e marcados como **não
observados**.

### Buying Signal — 25

Cinco famílias, 5 pontos cada: expansão, nova bandeira, mudança de liderança,
vaga de TI/marketing, replatform.

```
pontos = 5 × força × recência
```

**Teto por família.** Um mesmo evento noticiado em três portais não vale 15
pontos: vale 5. Sem o teto, uma cobertura de imprensa inflaria a dimensão
inteira. Os outros 20 pontos têm que vir de tipos de sinal diferentes.

**Recência.** Peso cheio até 90 dias, decaimento linear até zerar em um ano.
"O grupo abriu uma loja" é sinal de compra em março e ruído em dezembro — usar
sinal velho como gancho de abordagem produz exatamente a mensagem que o guardrail
de grounding existe para evitar.

### Contactability — 15

Decisor 5 · e-mail profissional 5 · telefone corporativo 3 · LinkedIn 2.

Contato inválido penaliza (2 pontos cada), com piso zero na dimensão: uma lista
de contatos ruim não pode derrubar Company Fit e Technology Pain junto.

## Faixas

| Total | Banda | Tier |
|---|---|---|
| 85–100 | `hot` | 1 |
| 70–84 | `high` | 2 |
| 50–69 | `medium` | 3 |
| < 50 | `nurture` | 4 |

## Cobertura — o número ao lado do número

`OpportunityScore.Coverage` é a fração dos pontos possíveis que veio de fato
observado.

Um score de 55 com 40% de cobertura é um **pedido de mais pesquisa**, não um
veredito. Site bom e site não auditado produzem os mesmos zero pontos de
performance — a cobertura é o que distingue os dois, e é ela que decide se vale
pesquisar mais ou descartar a conta.

Por isso todo `ScoreComponent` carrega `Observed`. `Points = 0` com
`Observed = false` significa "ainda não sabemos"; com `Observed = true` significa
"olhamos e não tem".

## O breakdown vai para o banco

`account_scores.feature_snapshot` guarda o breakdown completo em `jsonb`, em
snake_case como o resto do schema:

```sql
select feature_snapshot -> 'breakdown'
  from v_account_current_score
 where account_id = '...';
```

É o que responde, meses depois, "por que esta conta valia 68?". Sem ele, o score
é um número sem defesa.

## Recalcular não regride a conta

`researched → scored` acontece uma vez. Recalcular o score de uma conta já
contatada é normal — chegou um sinal novo —, mas empurrá-la de volta para
`scored` apagaria o fato de que ela já recebeu abordagem.

Conta em `suppressed` não pontua: não há fila de execução para priorizar.

## Limitação conhecida

Hoje o score é honesto e incompleto. Sem o Website Auditor (A03) e sem o People
Finder (A05), 45 dos 100 pontos são estruturalmente inalcançáveis, e nenhuma
conta passa de `medium` só com pesquisa.

Isso é a leitura correta do estado do sistema, não um defeito do score — e é
exatamente o que a cobertura reporta.
