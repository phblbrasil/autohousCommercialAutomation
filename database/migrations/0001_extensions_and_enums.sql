-- 0001 | Extensoes e tipos enumerados.
-- Os enums espelham exatamente o dominio em AutoHous.Revenue.Domain/Enums.

create extension if not exists pgcrypto;

create type account_status as enum (
  'discovered', 'researching', 'researched', 'scored', 'ready',
  'contacted', 'engaged', 'nurture', 'suppressed', 'customer', 'rejected'
);

create type contact_status as enum ('discovered', 'verified', 'invalid', 'suppressed');

-- Os estagios de negociacao vivem AQUI e nao em account_status: o diagrama do
-- blueprint (secao 18) funde dois ciclos de vida distintos. Uma account pode ter
-- varias oportunidades simultaneas em estagios diferentes.
create type opportunity_stage as enum (
  'meeting', 'sql', 'discovery', 'proposal', 'negotiation', 'won', 'lost'
);

create type evidence_type as enum (
  'company_registry', 'website', 'search', 'social', 'news',
  'job_posting', 'marketplace', 'manual', 'other'
);
