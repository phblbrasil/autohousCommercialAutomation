-- 0020 | O denominador de cada percentual fica visível.
--
-- A view da 0019 calcula `pct_bloqueia_busca_ia` com `avg`, e `avg` ignora NULL.
-- Site sem robots.txt legível entra como NULL e sai da conta — o que está certo,
-- porque "não verifiquei" não é "não bloqueia". O problema é que a coluna `n`
-- ao lado mostra o total do estrato, e não esse subconjunto.
--
-- Quem lê divide errado sem perceber. Numa amostra onde metade dos sites não
-- responde robots.txt, "30% bloqueiam busca de IA" pode significar 30% de 40 ou
-- 30% de 8 — e a segunda não sustenta decisão nenhuma.
--
-- Isso contradiz o próprio ADR-0013, que decidiu mostrar `n` junto de cada
-- percentil justamente para que estrato pequeno se denuncie. Um denominador
-- escondido é a mesma falha por outro caminho.

-- `drop` e nao `create or replace`: o Postgres so deixa o replace ACRESCENTAR
-- coluna no fim da lista, e as duas novas entram no meio - ao lado do `n`, que
-- e onde elas significam alguma coisa. Empurra-las para o fim so para caber na
-- restricao da ferramenta deixaria o denominador longe do numerador, que e
-- exatamente o problema que esta migration existe para resolver.
drop view if exists v_probe_distribution;

create view v_probe_distribution as
select
  natureza,
  porte,
  probe_version,
  count(*)                                                    as n,
  count(*) filter (where reached)                             as n_alcancados,

  -- O denominador de cada família de percentual, explícito.
  count(*) filter (where probe ->> 'aiSearchCrawlersBlocked' is not null)
                                                              as n_com_robots,
  count(*) filter (where probe ->> 'rawTextWords' is not null) as n_com_html,

  -- Descoberta por motor generativo
  avg(((probe ->> 'aiSearchCrawlersBlocked')::int > 0)::int)::numeric(5,4)
                                                              as pct_bloqueia_busca_ia,
  avg((probe ->> 'hasLlmsTxt')::boolean::int)::numeric(5,4)   as pct_com_llms_txt,
  avg((probe ->> 'isIndexable')::boolean::int)::numeric(5,4)  as pct_indexavel,
  percentile_cont(0.5) within group (order by (probe ->> 'rawTextWords')::int)
                                                              as mediana_palavras,

  -- Legibilidade por máquina
  avg((probe ->> 'structuredDataHasNap')::boolean::int)::numeric(5,4)
                                                              as pct_com_nap,
  avg((probe -> 'structuredDataTypes' ? 'Vehicle')::int)::numeric(5,4)
                                                              as pct_com_vehicle,

  -- Desempenho, que é custo de mídia
  percentile_cont(0.5) within group (order by (probe ->> 'timeToFirstByteMs')::numeric)
                                                              as mediana_ttfb_ms,
  percentile_cont(0.9) within group (order by (probe ->> 'timeToFirstByteMs')::numeric)
                                                              as p90_ttfb_ms,
  percentile_cont(0.5) within group (order by (probe ->> 'documentBytes')::numeric)
                                                              as mediana_bytes

from probe_samples
where reached
group by natureza, porte, probe_version;

comment on view v_probe_distribution is
  'Distribuição por estrato para o cálculo de severidade (ADR-0013). Cada '
  'percentual tem seu denominador ao lado: `n_com_robots` para as medidas que '
  'dependem do robots.txt, `n_com_html` para as que dependem do documento. '
  'Percentual sem denominador visível convida a divisão errada.';
