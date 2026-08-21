# ADR-0003 — Dapper e SQL explícito em vez de ORM

**Data:** 2026-08-20 (registro de decisão anterior) · **Status:** Aceito

## Contexto

O modelo é write-heavy, usa `jsonb`, arrays de `uuid`, enums nativos do Postgres,
colunas geradas com `tsvector` e índices GIN e de trigrama. As migrations são SQL
puro, numerado e imutável, executadas por DbUp.

Três construções do caminho crítico não se expressam bem em ORM:

- `for update skip locked` no claim do outbox — sem isso, dois workers pegam o
  mesmo evento e a pesquisa é executada, e paga, duas vezes;
- `on conflict (...) where <predicado>` sobre índice parcial — usado em `sources`,
  `contacts` e na fila de revisão de merge;
- `unnest` de arrays paralelos para inserir um lote inteiro em um comando.

## Opções consideradas

1. **EF Core.** Ganha change tracking e LINQ. Perde: o schema passa a ter duas
   fontes de verdade (migrations SQL e o modelo), e as três construções acima
   viram `FromSqlRaw` — o ORM no meio sem entregar o que justifica tê-lo.
2. **Dapper sobre as mesmas migrations.**
3. **ADO.NET puro.** Sem a materialização do Dapper, cada leitura vira laço de
   `IDataReader`.

## Decisão

Opção 2. As migrations são a fonte única do schema; o Dapper só materializa.

## Consequências

**Positivas**

- Uma fonte de verdade para o schema. O que roda em teste é o que roda em
  produção, pelo mesmo migrator.
- SQL do caminho crítico é legível e revisável.
- Portabilidade direta para o Supabase.

**Negativas**

- Sem change tracking: toda escrita é explícita.
- Materialização é por convenção de nome. Um record **posicional** exige
  assinatura de construtor exata e falha em tempo de execução quando um tipo
  diverge — foi assim que `SignalRow(… DateTimeOffset)` quebrou contra
  `timestamptz`, que o Npgsql devolve como `DateTime`. Linhas de leitura usam
  propriedades, não parâmetros posicionais.

## Gatilho de revisão

Se surgir um módulo majoritariamente CRUD, com agregados profundos e pouca
consulta especializada, avaliar EF Core **para aquele módulo** — sem migrar o
restante.
