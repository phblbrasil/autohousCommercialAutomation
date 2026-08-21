-- 0007 | Outreach e funil. Ainda nao usado pelo slice de research, migrado
-- agora para que o esquema P0 esteja completo.

create table touchpoints (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  contact_id uuid references contacts(id) on delete set null,
  channel text not null,
  sequence_name text,
  sequence_step integer,
  direction text not null default 'outbound',
  status text not null,
  subject text,
  content text,
  content_hash text,
  external_id text,
  idempotency_key text unique,
  scheduled_at timestamptz,
  sent_at timestamptz,
  delivered_at timestamptz,
  replied_at timestamptz,
  created_by text,
  approved_by text,
  created_at timestamptz not null default now()
);

create index touchpoints_contact_idx on touchpoints(contact_id, created_at desc);
create index touchpoints_account_idx on touchpoints(account_id, created_at desc);

create table conversations (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  contact_id uuid references contacts(id) on delete set null,
  touchpoint_id uuid references touchpoints(id) on delete set null,
  channel text not null,
  external_message_id text,
  direction text not null,
  content text,
  intent text,
  sentiment text,
  confidence numeric(5,4),
  requires_human boolean not null default false,
  received_at timestamptz,
  created_at timestamptz not null default now()
);

create index conversations_account_idx on conversations(account_id, created_at desc);

-- Os estagios de negociacao vivem aqui, nao em accounts.status.
create table opportunities (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  primary_contact_id uuid references contacts(id) on delete set null,
  product text not null,
  stage opportunity_stage not null,
  title text,
  estimated_value numeric(18,2),
  probability numeric(5,2),
  owner_user_id uuid,
  next_step text,
  next_step_at timestamptz,
  opened_at timestamptz not null default now(),
  closed_at timestamptz,
  loss_reason text,
  metadata jsonb
);

create index opportunities_account_idx on opportunities(account_id);
create index opportunities_stage_idx on opportunities(stage);
