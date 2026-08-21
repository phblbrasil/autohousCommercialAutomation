-- 0008 | Governanca. Antes de QUALQUER acao de outbound a plataforma consulta
-- esta tabela (secao 25, Regra 2). Nunca o agente.

create table suppression_list (
  id uuid primary key default gen_random_uuid(),
  account_id uuid references accounts(id) on delete cascade,
  contact_id uuid references contacts(id) on delete cascade,
  channel text,
  reason text not null,
  starts_at timestamptz not null default now(),
  ends_at timestamptz,
  created_by text not null,
  created_at timestamptz not null default now(),
  check (account_id is not null or contact_id is not null)
);

create index suppression_active_idx
  on suppression_list(account_id, contact_id, channel, ends_at);
