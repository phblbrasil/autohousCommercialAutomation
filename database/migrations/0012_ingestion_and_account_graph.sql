-- 0012 | Captura de dados empresariais e resolucao de grupo economico.
--
-- Fecha a lacuna das etapas 01-03 do pipeline (Seed -> Normalize -> Account
-- Graph): ate aqui o unico caminho de entrada era POST /accounts, um CNPJ por
-- vez. As 300 contas do piloto entrariam a mao.
--
-- Tres tabelas com papeis distintos:
--   ingestion_batches       -- unidade de lote, com totais e lineage da fonte
--   companies_raw           -- linha crua, exatamente como chegou
--   account_merge_candidates-- fila de revisao humana do agrupamento

-- 1. Lote -----------------------------------------------------------------
create table ingestion_batches (
  id uuid primary key default gen_random_uuid(),
  source_name text not null,
  source_uri text,
  -- open: aceitando linhas | captured: linhas gravadas | resolved: grafo
  -- resolvido | failed: interrompido
  status text not null default 'open',
  total_rows integer not null default 0,
  accepted_rows integer not null default 0,
  duplicate_rows integer not null default 0,
  rejected_rows integer not null default 0,
  created_accounts integer not null default 0,
  attached_cnpjs integer not null default 0,
  review_candidates integer not null default 0,
  notes text,
  started_at timestamptz not null default now(),
  finished_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index ingestion_batches_status_idx on ingestion_batches(status, started_at desc);

-- 2. Linha crua -----------------------------------------------------------
-- A linha e gravada ANTES de qualquer interpretacao. Sem este estagio, uma
-- regra de normalizacao errada destroi o dado de origem e a unica saida e
-- reimportar a fonte inteira - se ela ainda existir. O frame 02 da V2 chama
-- isto de "manter lineage".
create table companies_raw (
  id uuid primary key default gen_random_uuid(),
  batch_id uuid not null references ingestion_batches(id) on delete cascade,
  row_number integer not null,
  payload jsonb not null,
  cnpj_raw text,
  -- SHA-256 do payload canonico. Reimportar o mesmo arquivo nao duplica linha.
  content_hash text not null,
  -- pending -> normalized | rejected | review
  status text not null default 'pending',
  rejection_reason text,
  account_id uuid references accounts(id) on delete set null,
  ingested_at timestamptz not null default now(),
  processed_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create unique index companies_raw_batch_hash_uq on companies_raw(batch_id, content_hash);
create index companies_raw_pending_idx on companies_raw(batch_id, status);
create index companies_raw_account_idx on companies_raw(account_id) where account_id is not null;

-- 3. Fila de revisao do agrupamento --------------------------------------
-- O quality gate do frame 04 da V2 exige confidence >= 0.80 para merge
-- automatico; abaixo disso vai para revisao. Esta tabela E a review queue.
--
-- Um falso merge e mais caro que um falso split: unir dois grupos economicos
-- distintos faz dois SDRs atacarem a mesma conta com teses erradas, e desfazer
-- exige reconstruir evidencias. Por isso a faixa de revisao e generosa.
create table account_merge_candidates (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  raw_id uuid references companies_raw(id) on delete set null,
  incoming_cnpj char(14) not null,
  incoming_name text not null,
  incoming_uf char(2),
  incoming_municipio text,
  similarity numeric(5,4) not null,
  reason text not null,
  -- pending -> approved | rejected
  status text not null default 'pending',
  decided_by text,
  decided_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index account_merge_candidates_pending_idx
  on account_merge_candidates(similarity desc)
  where status = 'pending';

-- Mesmo CNPJ nao pode gerar duas entradas pendentes para a mesma conta:
-- reprocessar o lote seria suficiente para entupir a fila de revisao.
create unique index account_merge_candidates_pending_uq
  on account_merge_candidates(account_id, incoming_cnpj)
  where status = 'pending';

-- 4. Triggers e RLS, seguindo a convencao da 0009 -------------------------
do $$
declare t text;
begin
  foreach t in array array['ingestion_batches','companies_raw','account_merge_candidates'] loop
    execute format(
      'create trigger %I_set_updated_at before update on %I
       for each row execute function set_updated_at()', t, t);

    execute format('alter table %I enable row level security', t);
  end loop;
end $$;
