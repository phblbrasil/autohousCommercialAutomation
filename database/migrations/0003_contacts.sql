-- 0003 | Contatos e canais.

create table contacts (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  full_name text not null,
  normalized_name text,
  job_title text,
  department text,
  seniority text,
  persona text,
  status contact_status not null default 'discovered',
  confidence numeric(5,4),
  source_url text,
  last_verified_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index contacts_account_idx on contacts(account_id);
create index contacts_persona_idx on contacts(persona);

-- CORRECAO sobre a secao 8: sem esta restricao o People Finder acumula
-- duplicatas silenciosamente a cada execucao. normalized_name e preenchido
-- por NameNormalizer no dominio.
create unique index contacts_identity_uq
  on contacts(account_id, normalized_name, coalesce(job_title, ''))
  where normalized_name is not null;

create table contact_channels (
  id uuid primary key default gen_random_uuid(),
  contact_id uuid not null references contacts(id) on delete cascade,
  channel text not null,
  value text not null,
  normalized_value text,
  verified boolean not null default false,
  verification_method text,
  confidence numeric(5,4),
  last_verified_at timestamptz,
  created_at timestamptz not null default now(),
  unique(contact_id, channel, normalized_value)
);
