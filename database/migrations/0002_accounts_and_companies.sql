-- 0002 | Account (entidade central) e o vinculo com CNPJs, lojas e marcas.
-- Uma account representa a organizacao comercial; NUNCA tratar cada CNPJ como
-- oportunidade independente (secao 6 do blueprint).

create table accounts (
  id uuid primary key default gen_random_uuid(),
  name text not null,
  normalized_name text,
  domain text,
  segment text,
  tier smallint,
  state char(2),
  city text,
  status account_status not null default 'discovered',
  employee_range text,
  store_count integer,
  vehicle_inventory_estimate integer,
  annual_revenue_estimate numeric(18,2),
  parent_account_id uuid references accounts(id),
  graph_confidence numeric(5,4),
  research_completeness numeric(5,4),
  last_researched_at timestamptz,
  next_research_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create unique index accounts_domain_uq on accounts(lower(domain)) where domain is not null;
create index accounts_status_idx on accounts(status);
create index accounts_state_idx on accounts(state);
create index accounts_tier_idx on accounts(tier);

create table companies_cnpj (
  id uuid primary key default gen_random_uuid(),
  account_id uuid references accounts(id) on delete set null,
  cnpj char(14) not null unique,
  razao_social text,
  nome_fantasia text,
  cnae_principal text,
  cnaes_secundarios jsonb,
  situacao_cadastral text,
  natureza_juridica text,
  porte text,
  municipio text,
  uf char(2),
  data_abertura date,
  source_updated_at timestamptz,
  raw_payload jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index companies_cnpj_account_idx on companies_cnpj(account_id);
create index companies_cnpj_cnae_idx on companies_cnpj(cnae_principal);
create index companies_cnpj_uf_idx on companies_cnpj(uf);

create table account_locations (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  company_cnpj_id uuid references companies_cnpj(id) on delete set null,
  location_type text,
  name text,
  address text,
  city text,
  state char(2),
  latitude numeric(10,7),
  longitude numeric(10,7),
  is_active boolean not null default true,
  confidence numeric(5,4),
  created_at timestamptz not null default now()
);

create index account_locations_account_idx on account_locations(account_id);

create table account_brands (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  brand text not null,
  relationship text,
  confidence numeric(5,4),
  evidence_id uuid, -- FK adicionada em 0004, apos a criacao de evidence
  created_at timestamptz not null default now(),
  unique(account_id, brand)
);
