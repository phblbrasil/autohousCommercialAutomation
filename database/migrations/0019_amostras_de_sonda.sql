-- 0019 | Amostras de sonda: a base de calibração do ADR-0013.
--
-- A severidade de um achado passa a sair do percentil do segmento, e não de
-- constante inventada. Para isso é preciso conhecer a distribuição real do
-- mercado — e conhecê-la exige medir muitos sites que NÃO são contas do funil.
--
-- Por que tabela própria, e não `website_audits`:
--
--   `website_audits` é auditoria DE CONTA. Cada linha pertence a um
--   research_run, alimenta Technology Pain e sustenta uma abordagem comercial.
--   Uma amostra de calibração não é nada disso: é medição de população, feita
--   sobre um domínio ADIVINHADO a partir do e-mail da Receita, que pode nem ser
--   o site da empresa.
--
--   Misturar as duas contaminaria o funil de duas formas. A conta ganharia
--   auditoria que ninguém pediu, com domínio não confirmado; e a distribuição
--   ficaria enviesada pelas contas que já passaram pelo Researcher, que são
--   justamente as menos representativas — foram escolhidas a dedo.
--
-- O custo de modelo é zero: a sonda é HTTP puro.

create table probe_samples (
  id uuid primary key default gen_random_uuid(),

  -- A conta de onde veio o candidato. `set null` e não `cascade`: a medição do
  -- mercado continua válida como estatística mesmo que a conta suma.
  account_id uuid references accounts(id) on delete set null,

  -- Domínio efetivamente sondado, e de onde ele veio. A origem fica gravada
  -- porque a taxa de acerto de cada método é, ela própria, um resultado: se o
  -- domínio do e-mail raramente for o site, a amostra não representa nada, e é
  -- isso que o gatilho de revisão do ADR-0013 vigia.
  domain text not null,
  domain_source text not null,

  -- Estratos. Copiados no momento da amostra, e não lidos por join na hora da
  -- consulta: a distribuição precisa ser reproduzível meses depois, e o porte de
  -- uma empresa muda entre releases da Receita.
  natureza text,
  porte text,
  unidades integer,
  uf character(2),

  sampled_at timestamptz not null default now(),

  -- Versão da sonda. Sem isso, comparar percentil de safras diferentes seria
  -- comparar réguas diferentes - a v2 mede coisas que a v1 não media.
  probe_version text not null,

  reached boolean not null,
  status_code integer,

  -- A medição CRUA e inteira. Mesma razão de `website_audits.probe` e de
  -- `account_scores.feature_snapshot`: quando a fórmula do percentil mudar,
  -- recalcular a safra antiga tem que ser possível sem sondar tudo de novo -
  -- e sondar de novo significa bater em 14 mil sites de terceiros outra vez.
  probe jsonb not null
);

-- Um domínio, uma amostra por safra de sonda. Reexecutar a calibração não
-- duplica; atualiza pela chave.
create unique index probe_samples_domain_version_uq
  on probe_samples(domain, probe_version);

-- A consulta da calibração é sempre "distribuição dentro do estrato".
create index probe_samples_estrato_idx
  on probe_samples(natureza, porte, reached);

comment on table probe_samples is
  'Medição de população para calibrar severidade (ADR-0013). NÃO é auditoria de '
  'conta: o domínio é adivinhado do e-mail da Receita e pode não ser o site da '
  'empresa. Não alimenta Technology Pain nem abordagem comercial.';

-- ---------------------------------------------------------------------------
-- A distribuição, por estrato
-- ---------------------------------------------------------------------------
-- View e não tabela: é leitura pura sobre dezenas de milhares de linhas, e
-- materializar exigiria decidir quando invalidar - decisão que ninguém precisa
-- tomar enquanto a consulta custa milissegundos.
--
-- `n` sai junto de cada percentil de propósito. Estrato com 12 amostras não
-- sustenta um p90, e quem lê a distribuição precisa ver isso na mesma linha em
-- vez de descobrir depois.

create view v_probe_distribution as
select
  natureza,
  porte,
  probe_version,
  count(*)                                                    as n,
  count(*) filter (where reached)                             as n_alcancados,

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
  'Distribuição por estrato para o cálculo de severidade. `n` acompanha cada '
  'linha porque estrato pequeno não sustenta percentil, e quem lê precisa ver '
  'isso antes de usar o número.';

alter table probe_samples enable row level security;
