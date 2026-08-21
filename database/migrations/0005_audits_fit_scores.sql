-- 0005 | Auditoria de site, product fit e score.
-- product_fit e account_scores sao append-only: guardamos toda safra para
-- reproduzir qualquer decisao historica (ADR-004). A leitura do valor vigente
-- e feita pelas views criadas em 0009.

create table website_audits (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  url text not null,
  performance_score numeric(5,2),
  seo_score numeric(5,2),
  ux_score numeric(5,2),
  mobile_score numeric(5,2),
  conversion_score numeric(5,2),
  inventory_score numeric(5,2),
  tracking_score numeric(5,2),
  issues jsonb,
  strengths jsonb,
  -- Divida consciente: array sem integridade referencial. Ver docs/data-model.md.
  evidence_ids uuid[],
  audited_at timestamptz not null default now(),
  agent_run_id uuid
);

create index website_audits_account_idx on website_audits(account_id, audited_at desc);

create table product_fit (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  product text not null,
  score numeric(5,2) not null,
  reasons jsonb not null,
  objections jsonb,
  recommended_personas jsonb,
  recommended_entry boolean not null default false,
  model_version text,
  calculated_at timestamptz not null default now()
);

create index product_fit_account_idx on product_fit(account_id);
create index product_fit_product_score_idx on product_fit(product, score desc);

create table account_scores (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,
  company_fit numeric(5,2) not null default 0,
  technology_pain numeric(5,2) not null default 0,
  buying_signal numeric(5,2) not null default 0,
  contactability numeric(5,2) not null default 0,
  total_score numeric(5,2) not null,
  scoring_version text not null,
  feature_snapshot jsonb not null,
  calculated_at timestamptz not null default now()
);

create index account_scores_total_idx on account_scores(total_score desc);
create index account_scores_account_idx on account_scores(account_id, calculated_at desc);
