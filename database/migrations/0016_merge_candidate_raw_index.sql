-- 0016 | Indice na chave estrangeira account_merge_candidates.raw_id.
--
-- A 0012 criou `raw_id uuid references companies_raw(id)` e nao o indexou. O
-- Postgres nao cria indice para o lado que REFERENCIA - ele indexa o lado
-- referenciado, que ja e chave primaria. Sem este indice, toda remocao de linha
-- em companies_raw varre account_merge_candidates inteira para checar a FK.
--
-- Nao e teoria. Ao reiniciar a carga nacional de 2026-08 foi preciso apagar um
-- lote incompleto:
--
--     delete from companies_raw where batch_id = '...'   -- 839.409 linhas
--
-- O comando rodou DEZ MINUTOS sem terminar, sem estar bloqueado por ninguem:
-- 839 mil verificacoes de FK, cada uma um seq scan sobre 11 mil candidatos de
-- merge. Foi resolvido na hora com TRUNCATE, que nao checa FK linha a linha -
-- mas TRUNCATE so serve quando se pode apagar a tabela toda, e o caso geral
-- (apagar UM lote entre varios) e exatamente o que nao pode travar.
--
-- E o terceiro defeito da mesma familia neste schema, e vale nomear o padrao:
-- todos sao invisiveis na escala em que o codigo foi escrito e dominantes na
-- escala em que ele roda.
--
--   0013  indice sobre left(cnpj, 8), sem o qual FindCandidatesAsync fazia
--         seq scan por linha do lote
--   0015  (nao e indice, mas mesma origem) colunas de Technology Pain que o
--         dominio lia e o schema nao tinha
--   0016  esta - FK sem indice, custo zero com dezenas de linhas e quadratico
--         com centenas de milhares
--
-- Um lote pode ter centenas de milhares de linhas cruas; a fila de revisao
-- cresce junto. O produto das duas e o que este indice remove.

create index account_merge_candidates_raw_idx
  on account_merge_candidates(raw_id)
  where raw_id is not null;

comment on index account_merge_candidates_raw_idx is
  'Sustenta a checagem de FK ao apagar linhas de companies_raw. Ver 0016.';
