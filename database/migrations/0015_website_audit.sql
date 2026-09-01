-- 0015 | Website Auditor (A03).
--
-- Fecha tres pontas que a analise de lacunas deixou abertas em nome desta
-- entrega (docs/gap-analysis.md secao 6):
--
--   1. `technologies`, a unica tabela P0/P1 do frame 03 da V2 que nunca existiu;
--   2. `website_audits.evidence_ids uuid[]`, divida consciente assumida na 0005
--      com a nota "vira tabela de ligacao quando o auditor existir";
--   3. as duas colunas que OpportunityScoring ja le como fato de Technology Pain
--      - MultiplePortals e ComplexIntegration - e que nunca tiveram onde morar.
--
-- O ponto 3 merece nota: o dominio declara esses dois campos em
-- WebsiteAuditFacts desde o scoring, e AccountScoreRepository simplesmente nao
-- os seleciona, porque nao havia coluna. A dimensao inteira vinha nula. Nao era
-- bug de leitura; era schema faltando.

-- ---------------------------------------------------------------------------
-- 1. A auditoria ganha o que o score ja sabia consumir
-- ---------------------------------------------------------------------------

alter table website_audits
  -- Estoque publicado em mais de um portal: sintoma de fragmentacao, e um dos
  -- cinco criterios de Technology Pain do frame 06.
  add column multiple_portals boolean,
  add column complex_integration boolean,

  -- Medicao crua da sonda, guardada inteira. Os sete scores acima sao derivados
  -- dela por aritmetica deterministica (WebsiteAuditScoring); guardar so o
  -- derivado impediria recalcular uma safra antiga quando a formula mudar - o
  -- mesmo motivo que faz account_scores guardar feature_snapshot.
  add column probe jsonb,

  -- Qual research_run produziu esta auditoria. agent_run_id ja existia e aponta
  -- para a chamada do modelo; este aponta para a execucao de negocio.
  add column research_run_id uuid references research_runs(id) on delete set null,

  -- A URL efetivamente auditada depois de redirects. `url` guarda a pedida.
  add column final_url text,
  add column status text not null default 'completed';

comment on column website_audits.probe is
  'Medicao determinista da sonda (IWebsiteProbe), antes de virar score.';

-- ---------------------------------------------------------------------------
-- 2. evidence_ids uuid[] -> tabela de ligacao
-- ---------------------------------------------------------------------------
-- O array nunca teve integridade referencial: uma evidencia apagada deixava um
-- uuid orfao apontando para o nada, e "quais auditorias citam esta evidencia?"
-- exigia varrer arrays. A 0005 registrou isso como divida em vez de esconder.

create table website_audit_evidence (
  website_audit_id uuid not null references website_audits(id) on delete cascade,
  evidence_id uuid not null references evidence(id) on delete cascade,
  primary key (website_audit_id, evidence_id)
);

create index website_audit_evidence_evidence_idx
  on website_audit_evidence(evidence_id);

-- Migra o que houver antes de derrubar a coluna. Hoje a tabela esta vazia, mas
-- uma migration que assume o estado do banco de quem a escreveu e uma migration
-- que quebra no ambiente de outra pessoa.
insert into website_audit_evidence (website_audit_id, evidence_id)
select w.id, e.evidence_id
from website_audits w
cross join lateral unnest(w.evidence_ids) as e(evidence_id)
where w.evidence_ids is not null
  and exists (select 1 from evidence ev where ev.id = e.evidence_id)
on conflict do nothing;

alter table website_audits drop column evidence_ids;

-- ---------------------------------------------------------------------------
-- 3. technologies - a tabela P1 ausente
-- ---------------------------------------------------------------------------
-- Serve a duas perguntas diferentes, e por isso guarda `source`:
--
--   "o que da para MEDIR no HTML desta empresa?"        source = 'probe'
--   "o que o agente INFERIU da operacao dela?"          source = 'agent'
--
-- Sem essa distincao, um pixel de GA4 encontrado por regex e um "eles usam
-- Salesforce" deduzido de uma vaga de emprego valeriam o mesmo na hora de dizer
-- que a integracao e complexa - e so o primeiro e verificavel.

create table technologies (
  id uuid primary key default gen_random_uuid(),
  account_id uuid not null references accounts(id) on delete cascade,

  -- 'analytics', 'tag_manager', 'ads', 'crm', 'dms', 'chat', 'cms',
  -- 'inventory_platform', 'marketplace', 'ecommerce', 'other'.
  -- Texto e nao enum: a lista de categorias vai mudar mais rapido que o schema,
  -- e um enum novo exige migration para cada descoberta.
  category text not null,
  name text not null,
  version text,

  confidence numeric(5,4) not null default 1.0,

  -- 'probe' (medido no HTML) ou 'agent' (inferido, com evidencia).
  source text not null,

  -- Regra 1 da governanca: o que o agente afirma precisa de lastro. Nulo so e
  -- aceitavel quando source = 'probe', porque ai a propria medicao e a fonte.
  evidence_id uuid references evidence(id) on delete set null,
  website_audit_id uuid references website_audits(id) on delete set null,

  first_detected_at timestamptz not null default now(),
  last_detected_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),

  constraint technologies_source_check check (source in ('probe', 'agent')),
  constraint technologies_agent_needs_evidence
    check (source = 'probe' or evidence_id is not null)
);

-- Identidade da tecnologia dentro da conta. Sem ela, cada reauditoria duplicaria
-- a pilha inteira - o mesmo defeito que a 0003 corrigiu em contacts, a 0010 em
-- account_locations e a 0014 em company_partners.
create unique index technologies_identity_uq
  on technologies(account_id, category, lower(name));

create index technologies_account_idx on technologies(account_id);
create index technologies_name_idx on technologies(lower(name));

create trigger technologies_set_updated_at before update on technologies
  for each row execute function set_updated_at();

-- ---------------------------------------------------------------------------
-- 4. RLS - a 0009 ligou em tudo que existia naquele momento
-- ---------------------------------------------------------------------------

alter table technologies            enable row level security;
alter table website_audit_evidence  enable row level security;
