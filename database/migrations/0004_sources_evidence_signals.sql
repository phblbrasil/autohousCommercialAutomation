-- 0004 | Evidence-first: nenhuma afirmacao comercial sem fonte rastreavel
-- (secao 10 do blueprint). sources = o documento observado; evidence = a
-- afirmacao extraida dele; signals = interpretacao com validade temporal.

create table sources (
  id uuid primary key default gen_random_uuid(),
  source_type evidence_type not null,
  url text,
  title text,
  domain text,
  observed_at timestamptz not null default now(),
  fetched_at timestamptz,
  content_hash text,
  raw_ref text,
  metadata jsonb
);

create index sources_url_idx on sources(url);

-- Dedup de fontes: repesquisar a mesma pagina nao deve inflar a tabela.
create unique index sources_content_hash_uq on sources(content_hash) where content_hash is not null;

create table evidence (
  id uuid primary key default gen_random_uuid(),
  account_id uuid references accounts(id) on delete cascade,
  contact_id uuid references contacts(id) on delete cascade,
  source_id uuid not null references sources(id) on delete cascade,
  claim_type text not null,
  claim_text text not null,
  extracted_value jsonb,
  confidence numeric(5,4),
  valid_from timestamptz,
  valid_until timestamptz,
  created_at timestamptz not null default now()
);

create index evidence_account_idx on evidence(account_id);
create index evidence_claim_idx on evidence(claim_type);

-- FK adiada de 0002: account_brands nasce antes de evidence existir.
alter table account_brands
  add constraint account_brands_evidence_fk
  foreign key (evidence_id) references evidence(id) on delete set null;

create table signals (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  signal_type text not null,
  strength numeric(5,4) not null,
  title text,
  description text,
  evidence_id uuid references evidence(id) on delete set null,
  observed_at timestamptz not null,
  expires_at timestamptz,
  created_at timestamptz not null default now()
);

create index signals_account_idx on signals(account_id);
create index signals_type_idx on signals(signal_type);
create index signals_observed_idx on signals(observed_at desc);
