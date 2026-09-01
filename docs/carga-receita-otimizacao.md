# Carga da Receita — diagnóstico e plano de otimização

**01/09/2026.** Documento de planejamento. Nada aqui foi aplicado ainda, exceto o
que está marcado como **✅ já aplicado**.

---

## 1. O que está acontecendo

A carga nacional do release `2026-08` dos Dados Abertos CNPJ tem três fases:

| Fase | O que faz | Situação |
|---|---|---|
| **Download** | 26 arquivos, 6,7 GB | ✅ concluída, em cache |
| **Passada A–C** | lê 72,8 milhões de estabelecimentos, agrega mercado, casa com `Empresas` e `Simples` | ✅ concluída |
| **Resolução** | para cada uma das 839.409 linhas do universo automotivo, decide onde ela entra no account graph | 🔄 **47%, lenta** |

A terceira fase é a que dói. Ela roda **uma transação por linha** — decisão
correta, documentada no caso de uso: um lote em transação única seguraria locks
por minutos e perderia tudo em qualquer erro.

O problema não é o desenho. É que cada linha estava custando ~96 ms.

---

## 2. Como cheguei aqui

Vale registrar a sequência, porque cada passo corrigiu uma hipótese do anterior —
e duas delas eu afirmei com confiança antes de medir.

### 2.1 Primeiro suspeito: o limiar do trigrama ✅ já aplicado

A busca por nome parecido usa o operador `%` do `pg_trgm`. Ele **não** usa o
limiar da cláusula `similarity() >= 0.75` — usa a GUC
`pg_trgm.similarity_threshold`, cujo padrão é **0.30**.

Os dois discordavam. O índice devolvia 19.253 candidatos, o recheck descartava
17.875, o filtro derrubava outros 979, e sobravam 399.

```
antes:  147 ms   3.893 blocos de heap
depois: 11,6 ms     51 blocos
```

**Correção:** `set_limit(@MinSimilarity)` por conexão em
`AccountGraphRepository.FindCandidatesAsync`, usando o mesmo parâmetro da
cláusula — assim os dois não têm como divergir. Por conexão e não `alter
database`, porque a GUC mudaria a semântica do `%` no banco inteiro.

### 2.2 Segundo: FK sem índice ✅ já aplicado (migration 0016)

Ao precisar apagar um lote incompleto, o `DELETE` de 839 mil linhas rodou **dez
minutos sem terminar**, sem estar bloqueado.

`account_merge_candidates.raw_id` referencia `companies_raw(id)` e não tinha
índice. O Postgres indexa o lado *referenciado*, nunca o que *referencia* — então
cada linha apagada varria a fila de revisão inteira.

### 2.3 Terceiro: a máquina dormindo ✅ já aplicado

A primeira execução completa morreu depois de 12 horas e 47%:

```
Npgsql.NpgsqlException: The operation has timed out
  at NpgsqlConnector.ConnectAsync -> RawOpen -> OpenNewConnector
```

O log de eventos do Windows fecha o caso:

```
09:31:41  The system is exiting Modern Standby
09:31:43  crash do ingestor
```

A máquina passou a noite entrando e saindo de Modern Standby. Ao acordar, as
conexões do pool estavam mortas e a reconexão estourou o timeout de 15 s.

**Correções:** `powercfg` desativando standby em AC e bateria (reverter depois
com `powercfg /change standby-timeout-dc 60`), e **retry com espera crescente**
em `NpgsqlConnectionFactory.OpenAsync` — 5 tentativas, 1s→8s. O defeito real não
foi o soluço: foi um job de horas não sobreviver a um soluço.

> A sonda de `/health` passa `retryTransient: false`. Uma liveness probe que
> insiste 15 s faria o Railway reiniciar um serviço que só esperava o banco.

### 2.4 Onde eu errei

Duas afirmações que fiz e tive de corrigir:

- **"A queda de ritmo é o crescimento da tabela."** Depois disse que era só o
  standby. **Eram os dois** — e nenhum era a causa principal, como a seção 3
  mostra.
- **"Corrigir o limiar dá 12x."** Deu ~3x na prática. O ganho de 12x era da
  consulta isolada; a linha inteira faz muito mais que buscar.

---

## 3. O diagnóstico atual, com medição

Amostrei 30 vezes o que o Postgres estava executando:

```
21/30   select account_id from companies_cnpj where cnpj = $1
 8/30   with por_raiz as (... trigrama ...)
 1/30   select set_limit($1)
```

**70% do tempo na consulta de CNPJ** — que eu havia medido em 1,1 ms. A proporção
não fechava. O motivo:

```sql
-- companies_cnpj.cnpj é character(14)

prepare como_text(text)      -- é o que o Npgsql envia para um string do C#
  as select account_id from companies_cnpj where cnpj = $1;
-- Parallel Seq Scan, 50,7 ms, varre 355 mil linhas
-- Filter: ((cnpj)::text = '...'::text)   ← o cast anula o índice

prepare como_bpchar(character(14))
  as select account_id from companies_cnpj where cnpj = $1;
-- Index Scan, 0,86 ms
```

**59x.** O Dapper manda `string`, o Npgsql infere `text`, o Postgres reescreve
para `(cnpj)::text = $1::text`, e um índice sobre `cnpj` não serve mais.

E isto **piora sozinho**: é um seq scan sobre uma tabela que ganha uma linha a
cada linha processada. É a explicação real da degradação 1.933 → 1.203 → 564
linhas/min — melhor que o trigrama, melhor que o standby.

### Orçamento por linha, hoje

```
consulta de CNPJ (seq scan)      ~50 ms    ← 3.1
trigrama (já corrigido em 2.1)   ~46 ms    ← 3.2
resto (insert, commit, pool)      ~5 ms
                                 ───────
                                  ~96 ms   = 624 linhas/min
```

---

## 4. Opções, com ganho medido e custo

### Opção 1 — Cast explícito nas comparações de largura fixa · **recomendada**

```sql
where cnpj = cast(@Cnpj as char(14))   -- Index Scan, 0,076 ms
```

Quatro pontos no código:

| Arquivo | Uso |
|---|---|
| `AccountGraphRepository.cs:301` | **o quente**, uma vez por linha da carga |
| `AccountRepository.cs:40` e `:57` | API |
| `CompanyPartnerRepository.cs:96` | sócios (`cnpj_basico`, `char(8)`) |

- **Ganho:** ~50 ms → ~0,1 ms por linha. Estimado **96 → ~46 ms/linha**, ~2x.
- **Custo:** nenhum. Não muda resultado, não muda schema.
- **Bônus:** interrompe a degradação — o custo deixa de crescer com a tabela.

Colunas de largura fixa no schema, todas candidatas ao mesmo problema:
`companies_cnpj.cnpj/uf`, `company_partners.cnpj_basico`,
`account_merge_candidates.incoming_cnpj/incoming_uf`, `accounts.state`,
`account_locations.state`.

### Opção 2 — `Max Auto Prepare` na connection string

Medi `Planning Time: 1,66 ms` contra `Execution Time: 1,13 ms` — planejar custa
mais que executar. Com 3–4 consultas por linha, são ~5 ms de planejamento
desperdiçado.

- **Ganho:** estimado 5–8% depois da Opção 1.
- **Custo:** uma linha de configuração. Precisa de reinício do processo.

### Opção 3 — `fastupdate = off` no índice de trigrama · ✅ já aplicado

O GIN acumula inserções numa *pending list* varrida linearmente a cada busca.
Limpá-la deu 57,9 → 44,3 ms; desligar o `fastupdate` impede que ela reacumule.

Esta carga faz **uma busca por linha inserida** — é muito mais pesada em leitura
que em escrita, e o `fastupdate` otimiza o lado errado.

### Opção 4 — Índice composto por UF (`btree_gin`) · **não recomendada**

Cortaria o trigrama filtrando por estado antes.

- **Ganho real: ~3,8x, não 27x.** SP concentra 26,6% das contas, e o mercado
  automotivo se concentra ainda mais.
- **Custo: perde 29,3% da fila de revisão.** Conferi o `AccountGroupResolver`:
  similaridade alta em UF diferente **não é descartada** — vira
  `name_match_other_uf` e vai para revisão humana. São 14.461 casos hoje.

Isso não é otimização; é decisão de produto. Fica fora do plano técnico.

### Opção 5 — Lote de transações

Agrupar N linhas por transação amortizaria o commit.

- **Ganho:** desconhecido sem medir. O `resto` do orçamento é só ~5 ms, então o
  teto é baixo.
- **Custo:** perde a granularidade de falha que o caso de uso escolheu de
  propósito. **Não vale antes de medir**.

---

## 5. Plano recomendado

**Passo 1 — Aplicar a Opção 1.** É a única com ganho grande e custo zero. Mexe em
quatro linhas de SQL.

**Passo 2 — Rodar a suíte.** 545 testes. A mudança é de tipo de parâmetro, e os
testes de integração exercitam esses repositórios contra Postgres real.

**Passo 3 — Reiniciar a carga com `--resolve-batch`.** O processo atual roda do
binário antigo e não pega a correção. A retomada não redownloada, não relê os
zips e não duplica linha crua — só continua as ~443 mil pendentes.

**Passo 4 — Medir o ritmo real.** Se ficar em ~1.300/min, ETA cai de ~12 h para
~5,7 h. Só então decidir se a Opção 2 vale um reinício adicional.

**Passo 5 — Não tocar nas Opções 4 e 5** sem uma decisão explícita — a 4 é
produto, a 5 é otimização sem medição.

---

## 6. O que aprendi que vale além desta carga

Três defeitos, uma assinatura só: **invisíveis na escala em que o código foi
escrito, dominantes na escala em que ele roda.**

| | Defeito | Escala em que aparece |
|---|---|---|
| 0013 | índice sobre `left(cnpj, 8)` ausente | centenas de milhares de linhas |
| 0016 | FK sem índice em `raw_id` | ao apagar um lote |
| hoje | cast implícito anulando índice | tabela grande o bastante para o seq scan doer |

Todos passam por qualquer teste funcional. Nenhum aparece em um banco de
desenvolvimento com dezenas de linhas. O que os revela é rodar com volume real e
**olhar o plano de execução, não o relógio** — o relógio diz que está lento, o
plano diz por quê.
