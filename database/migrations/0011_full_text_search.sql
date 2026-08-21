-- 0011 | Busca full-text em portugues sobre contas, evidencias e sinais.
--
-- Duas ferramentas distintas, para problemas distintos:
--   FTS       -> "quais contas FALAM sobre expansao?"        (busca em texto)
--   trigrama  -> "qual razao social SE PARECE com esta?"     (casamento difuso)
-- Usar FTS para casar nomes de empresa daria resultado ruim: o stemmer e a
-- lista de stopwords existem para prosa, nao para razao social.

create extension if not exists unaccent;
create extension if not exists pg_trgm;

-- Configuracao propria: remove acentos ANTES de aplicar o stemmer portugues.
--
-- Necessaria porque unaccent() e apenas STABLE (depende de dicionario em disco) e
-- por isso nao pode aparecer em coluna gerada. Ja to_tsvector(regconfig, texto),
-- com a configuracao fixa, e IMMUTABLE - e e isso que torna a coluna gerada legal.
-- Efeito pratico: "veiculos" encontra "veículos", e "sao paulo" encontra "São Paulo".
create text search configuration portuguese_unaccent (copy = portuguese);

alter text search configuration portuguese_unaccent
  alter mapping for hword, hword_part, word
  with unaccent, portuguese_stem;

-- ---------------------------------------------------------------- accounts
-- Pesos: o nome vale mais que a cidade. ts_rank respeita essa hierarquia.
alter table accounts
  add column search_vector tsvector
  generated always as (
    setweight(to_tsvector('portuguese_unaccent', coalesce(name, '')),    'A') ||
    setweight(to_tsvector('portuguese_unaccent', coalesce(domain, '')),  'B') ||
    setweight(to_tsvector('portuguese_unaccent', coalesce(city, '')),    'C') ||
    setweight(to_tsvector('portuguese_unaccent', coalesce(segment, '')), 'D')
  ) stored;

create index accounts_search_idx on accounts using gin (search_vector);

-- ---------------------------------------------------------------- evidence
-- O caso de uso mais valioso: "quais contas tem evidencia de expansao?" deixa de
-- exigir leitura manual de cada research profile.
alter table evidence
  add column search_vector tsvector
  generated always as (
    setweight(to_tsvector('portuguese_unaccent', coalesce(claim_text, '')), 'A') ||
    setweight(to_tsvector('portuguese_unaccent', coalesce(claim_type, '')), 'B')
  ) stored;

create index evidence_search_idx on evidence using gin (search_vector);

-- ----------------------------------------------------------------- signals
alter table signals
  add column search_vector tsvector
  generated always as (
    setweight(to_tsvector('portuguese_unaccent', coalesce(title, '')),       'A') ||
    setweight(to_tsvector('portuguese_unaccent', coalesce(description, '')), 'B') ||
    setweight(to_tsvector('portuguese_unaccent', coalesce(signal_type, '')), 'C')
  ) stored;

create index signals_search_idx on signals using gin (search_vector);

-- ---------------------------------------------------- trigrama (account graph)
-- Base das features de similaridade da secao 11. Responde "que razoes sociais se
-- parecem com esta?" - o que FTS nao faz bem e que e exatamente o que o
-- clustering de grupo economico precisa.
create index accounts_name_trgm_idx
  on accounts using gin (normalized_name gin_trgm_ops);

create index companies_cnpj_razao_trgm_idx
  on companies_cnpj using gin (razao_social gin_trgm_ops);

create index companies_cnpj_fantasia_trgm_idx
  on companies_cnpj using gin (nome_fantasia gin_trgm_ops);
