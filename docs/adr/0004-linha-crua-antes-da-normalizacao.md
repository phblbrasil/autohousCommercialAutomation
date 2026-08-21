# ADR-0004 — Estágio de linha crua na captura

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

O pipeline de captura precisa transformar arquivos de base empresarial em contas
agrupadas. A tentação natural é normalizar durante a leitura e gravar só o que
serve.

Duas propriedades das fontes tornam isso perigoso:

- **elas mudam.** O extrato de agosto não é o de setembro; uma empresa some, uma
  situação cadastral muda. Reimportar não devolve o dado de ontem.
- **as regras vão errar.** CNAE novo, município grafado diferente, encoding
  inesperado, coluna trocada no arquivo de origem.

Se as duas coisas acontecem juntas — regra errada descoberta depois de a fonte
mudar —, o dado é irrecuperável.

## Opções consideradas

1. **Normalizar na leitura, gravar só o aproveitável.** Mais simples e mais
   barato. Perde a origem.
2. **Guardar o arquivo original em object storage** e normalizar na leitura.
   Preserva a origem, mas reprocessar exige reler arquivo, reparsear e
   reconciliar com o que já entrou.
3. **Estágio `companies_raw` no banco**, com o payload em `jsonb` e o resultado
   por linha.

## Decisão

Opção 3. `IngestCompanyBatchUseCase` **não interpreta nada**: não valida CNPJ,
não filtra CNAE, não decide grupo. Grava a linha e para.

`CompanyNormalizer` é função pura sobre `RawCompanyFields`. Reprocessar
`companies_raw` depois de corrigir uma regra produz resultado novo sem tocar na
fonte.

Cada linha carrega seu desfecho: `pending → normalized | rejected | review`, com
`rejection_reason` e `account_id` quando houver.

## Consequências

**Positivas**

- Corrigir uma regra é reprocessar, não reimportar.
- Lineage por linha: dá para responder "de qual lote veio esta conta?".
- `unknown_cnae` versus `outside_universe` distingue defeito de parsing de filtro
  funcionando — uma coluna mal mapeada não se disfarça de "não é nosso ICP".

**Negativas**

- Armazenamento duplicado: o payload fica em `companies_raw` e os campos úteis em
  `companies_cnpj`.
- Uma etapa a mais entre arquivo e conta.

**Custo real**

Um `jsonb` de sete campos por linha. Para as 300 contas do piloto é irrelevante;
para milhões de CNPJs, `companies_raw` vira candidata a particionamento por lote
ou a expurgo de lotes antigos já resolvidos.

## Gatilho de revisão

Quando `companies_raw` passar de ~10 GB, definir política de retenção por lote.
