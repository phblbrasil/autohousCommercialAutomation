-- 0006 | Execucao: runs de pesquisa, runs de agente e o outbox transacional.

create table research_runs (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  run_type text not null,
  status text not null,
  started_at timestamptz not null default now(),
  finished_at timestamptz,
  completeness numeric(5,4),
  result jsonb,
  error jsonb,
  created_at timestamptz not null default now()
);

create index research_runs_account_idx on research_runs(account_id, created_at desc);
create index research_runs_status_idx on research_runs(status);

-- Observabilidade da secao 28: custo por conta pesquisada e por reuniao gerada
-- sai daqui. agent_name e prompt_version sao rotulos NOSSOS: o Hermes nao
-- endereca agentes por nome.
create table agent_runs (
  id uuid primary key default gen_random_uuid(),
  account_id uuid references accounts(id) on delete cascade,
  contact_id uuid references contacts(id) on delete cascade,
  research_run_id uuid references research_runs(id) on delete set null,
  agent_name text not null,
  prompt_version text not null,
  model_provider text,
  model_name text,
  external_run_id text,
  status text not null,
  input_ref jsonb,
  output jsonb,
  input_tokens integer,
  output_tokens integer,
  estimated_cost numeric(14,6),
  started_at timestamptz not null default now(),
  finished_at timestamptz,
  error jsonb
);

create index agent_runs_account_idx on agent_runs(account_id);
create index agent_runs_agent_idx on agent_runs(agent_name, started_at desc);

-- Outbox (secao 20). idempotency_key identifica ESTA execucao e nao a janela de
-- repesquisa: fundir os dois conceitos, como sugere a secao 19, bloquearia o
-- retry apos falha. O cooldown mensal e checado explicitamente na API.
create table events_outbox (
  id uuid primary key default gen_random_uuid(),
  event_type text not null,
  aggregate_type text not null,
  aggregate_id uuid not null,
  payload jsonb not null,
  idempotency_key text not null unique,
  status text not null default 'pending',
  attempts integer not null default 0,
  available_at timestamptz not null default now(),
  processed_at timestamptz,
  last_error text,
  created_at timestamptz not null default now()
);

create index events_outbox_pending_idx
  on events_outbox(status, available_at) where status = 'pending';
