-- 0017 | Product Matcher (A04), People Finder (A05) e o Orchestrator (A01).
--
-- Fecha as tres pontas que a analise de lacunas deixou abertas depois da 0015
-- (docs/gap-analysis.md secao 6, itens 2, 3 e 7). O que falta de schema para
-- elas nao e tabela nova - `product_fit`, `contacts` e `contact_channels`
-- existem desde a 0003 e a 0005. Falta o que a 0005 nao previa porque os
-- agentes nao existiam:
--
--   1. lastro. `product_fit` e `contacts` nao tinham como apontar para as
--      evidencias que os sustentam, e a Regra 1 valia so para pesquisa e
--      auditoria;
--   2. rastreabilidade de execucao. Nenhuma das duas tabelas sabia qual
--      research_run ou agent_run a produziu - a mesma lacuna que a 0015 fechou
--      em website_audits;
--   3. o angulo comercial. `product_fit.reasons` guardava um array de texto, e
--      agora precisa guardar duas coisas de naturezas diferentes: a aritmetica
--      deterministica e o argumento escrito pelo agente.
--
-- O item 3 nao muda o tipo da coluna - ja e jsonb. Muda o que se escreve nela,
-- e a mudanca esta documentada aqui porque quem consultar a coluna precisa
-- saber que ha duas safras com formatos diferentes.

-- ---------------------------------------------------------------------------
-- 1. product_fit ganha lastro e execucao
-- ---------------------------------------------------------------------------

alter table product_fit
  -- Qual safra de score originou este fit. E a ancora de idempotencia do
  -- Product Matcher: refazer o fit sobre o mesmo score gastaria uma chamada de
  -- modelo para reescrever o mesmo argumento.
  add column account_score_id uuid references account_scores(id) on delete set null,

  add column research_run_id uuid references research_runs(id) on delete set null,
  add column agent_run_id uuid references agent_runs(id) on delete set null,

  -- Cobertura do diagnostico: a fracao dos pontos possiveis que veio de
  -- criterio observado. Mesmo papel do OpportunityScore.Coverage, e mesma
  -- razao: fit 80 apurado sobre 30% dos criterios e um palpite com casas
  -- decimais, e quem le a fila precisa poder distinguir os dois.
  add column coverage numeric(5,4),

  -- Confianca do AGENTE no argumento, que nao e a nota do fit. Nula quando o
  -- agente falhou e so a aritmetica foi gravada - estado previsto, e o unico
  -- dos quatro agentes cuja falha nao perde a etapa inteira.
  add column pitch_confidence numeric(5,4);

comment on column product_fit.reasons is
  'Objeto com tres chaves: `angle` (a frase de entrada, do agente), `criteria` '
  '(a aritmetica deterministica de ProductFitScoring, uma entrada por criterio) '
  'e `narrative` (os motivos escritos pelo agente, cada um com evidence_id). '
  'Safras anteriores a 0017 guardam um array simples.';

comment on column product_fit.model_version is
  'Versao do calculo deterministico (ProductFitScoring.Version), e nao do modelo '
  'de linguagem. Qual modelo escreveu o argumento esta em agent_runs.';

create index product_fit_entry_idx
  on product_fit(account_id, calculated_at desc)
  where recommended_entry;

-- Lastro do argumento. Tabela de ligacao e nao coluna uuid[] porque a 0015 ja
-- pagou o preco de descobrir que array nao tem integridade referencial.
create table product_fit_evidence (
  product_fit_id uuid not null references product_fit(id) on delete cascade,
  evidence_id uuid not null references evidence(id) on delete cascade,
  primary key (product_fit_id, evidence_id)
);

create index product_fit_evidence_evidence_idx
  on product_fit_evidence(evidence_id);

-- ---------------------------------------------------------------------------
-- 1b. website_audits guarda QUANTOS canais externos, e nao so "mais de um"
-- ---------------------------------------------------------------------------
-- A 0015 gravou `multiple_portals boolean`, derivado de `portals[]` da saida do
-- agente, e o array em si nao sobrevivia a persistencia - ficava so dentro de
-- `research_runs.result`, que e JSON de auditoria e nao de consulta.
--
-- Isso bastava para o Opportunity Score, que so pergunta "ha fragmentacao?". Nao
-- basta para o fit de MotorHub, que separa dois canais de quatro: o esforco de
-- manter o mesmo estoque coerente cresce com o numero deles, e "mais de um" nao
-- distingue o caso barato do caro.
--
-- A alternativa seria contar `technologies` de categoria 'marketplace'. Seria um
-- proxy que sub-reporta: o agente registra o portal em `portals[]` a partir de um
-- link no rodape, e so vira linha em `technologies` quando ha assinatura no HTML.

alter table website_audits
  add column portal_count integer;

comment on column website_audits.portal_count is
  'Quantos canais externos a auditoria encontrou. NULL = nao verificado; 0 = '
  'verificado e nao ha. A distincao e a mesma que separa `false` de `null` no '
  'resto da auditoria.';

-- ---------------------------------------------------------------------------
-- 2. contacts ganha lastro e execucao
-- ---------------------------------------------------------------------------
-- A tabela existe vazia desde a 0003, com a nota de que "quem popula e o People
-- Finder, sob a politica do frame 09". Este e o momento.

alter table contacts
  add column research_run_id uuid references research_runs(id) on delete set null,
  add column agent_run_id uuid references agent_runs(id) on delete set null,

  -- Como a plataforma classificou o cargo, com PersonaCatalog. A coluna
  -- `persona` guarda a classificacao NOSSA; esta guarda o que o agente sugeriu.
  -- Duas colunas e nao uma porque a divergencia entre as duas leituras e o
  -- sinal mais barato de que a taxonomia precisa de uma regra nova.
  add column agent_persona text,

  -- Piso de confianca do ContactPolicy, imposto no banco. O guard ja recusa o
  -- run inteiro antes de chegar aqui; este check existe para o caminho que o
  -- guard nao cobre - uma escrita manual, um script de correcao, uma carga.
  add constraint contacts_confidence_floor
    check (confidence is null or confidence >= 0.5);

comment on column contacts.confidence is
  'Certeza de que esta pessoa ocupa este cargo NESTA empresa HOJE. Piso de 0.5 '
  'imposto por check e por EvidenceFirstGuard (ContactPolicy).';

create table contact_evidence (
  contact_id uuid not null references contacts(id) on delete cascade,
  evidence_id uuid not null references evidence(id) on delete cascade,

  -- 'identity' (esta pessoa trabalha aqui neste cargo) ou o nome do canal
  -- ('email', 'phone', ...). A distincao e a Regra 1 desta etapa: achar o nome
  -- e achar o e-mail sao duas descobertas, com fontes diferentes, e o guard
  -- recusa o run quando o canal reaproveita a evidencia da identidade.
  claim_scope text not null default 'identity',

  primary key (contact_id, evidence_id, claim_scope)
);

create index contact_evidence_evidence_idx
  on contact_evidence(evidence_id);

-- ---------------------------------------------------------------------------
-- 3. contact_channels: profissional x pessoal
-- ---------------------------------------------------------------------------
-- A distincao decide se o canal conta como "e-mail profissional" nos 5 pontos
-- de contactability, e e a base legal da abordagem: um e-mail corporativo
-- publicado e contato de negocio; o Gmail pessoal achado num cadastro qualquer
-- e outra coisa.

alter table contact_channels
  add column is_professional boolean,

  -- O canal esta no dominio da propria conta. E o lastro mais forte de vinculo
  -- que existe sem depender do que o modelo afirmou.
  add column matches_account_domain boolean,

  add column evidence_id uuid references evidence(id) on delete set null,

  add constraint contact_channels_confidence_floor
    check (confidence is null or confidence >= 0.6);

comment on column contact_channels.is_professional is
  'false para provedor de e-mail pessoal (ContactPolicy.PersonalEmailProviders). '
  'Nao e bloqueio: revenda pequena opera com Gmail de verdade, e o canal entra '
  'sem contar como e-mail profissional na pontuacao.';

-- ---------------------------------------------------------------------------
-- 4. Sinais negativos do Product Matcher
-- ---------------------------------------------------------------------------
-- Desqualificador - recuperacao judicial, encerramento, mudanca de ramo - vira
-- linha em `signals` com forca negativa, e nao tabela propria: e um fato datado
-- com evidencia sobre a conta, que e exatamente o que `signals` guarda.
--
-- A coluna `strength` era `numeric(5,4)` sem sinal declarado. O check abaixo
-- abre o intervalo para -1 e documenta o que negativo significa.

alter table signals
  drop constraint if exists signals_strength_check;

alter table signals
  add constraint signals_strength_check
    check (strength >= -1 and strength <= 1);

comment on column signals.strength is
  'De -1 a 1. Positivo e sinal de compra (expansao, replatform, vaga). Negativo '
  'e desqualificador achado pelo Product Matcher: -1 tira a conta da fila quente '
  'e manda para revisao humana. NUNCA suprime sozinho - suppression e decisao de '
  'gente (Regra 2).';

-- ---------------------------------------------------------------------------
-- 5. O que o Orchestrator le
-- ---------------------------------------------------------------------------
-- Ele decide a partir do retrato INTEIRO da conta, numa leitura so. Seis
-- leituras independentes veriam seis instantes diferentes, e uma auditoria
-- concluindo entre a terceira e a quarta faria a decisao sair sobre um estado
-- que nunca existiu.
--
-- View e nao funcao: e leitura pura, e o planner cuida do resto.

create view v_account_progress as
select
  a.id                                   as account_id,
  a.status                               as status,
  a.domain is not null
    and length(trim(a.domain)) > 0       as has_domain,
  a.research_completeness                as research_completeness,
  a.last_researched_at                   as last_researched_at,
  a.next_research_at                     as next_research_at,
  a.tier                                 as tier,

  exists (
    select 1 from research_runs r
    where r.account_id = a.id and r.status in ('queued', 'running')
  )                                      as has_run_in_flight,

  (select max(w.audited_at) from website_audits w where w.account_id = a.id)
                                         as last_audited_at,

  s.id                                   as current_score_id,
  s.calculated_at                        as scored_at,

  f.id                                   as product_fit_batch_id,
  f.calculated_at                        as product_fit_at,
  coalesce(f.recommended_entry, false)   as has_recommended_entry,

  exists (
    select 1 from signals g
    where g.account_id = a.id
      and g.strength <= -1
      and (g.expires_at is null or g.expires_at > now())
  )                                      as has_blocking_disqualifier,

  -- Ultima BUSCA, e nao ultimo contato. A distincao impede o laco: uma busca
  -- que voltou vazia e um resultado, e sem esta data o Orchestrator pediria a
  -- mesma busca a cada evento que chegasse.
  (select max(r.finished_at) from research_runs r
    where r.account_id = a.id
      and r.run_type = 'contact_discovery'
      and r.status = 'completed')        as contacts_searched_at,

  exists (
    select 1 from contacts c
    where c.account_id = a.id
      and c.status <> 'invalid'
      and c.seniority in ('socio', 'c_level', 'diretor')
  )                                      as has_decision_maker

from accounts a
left join lateral (
  select sc.id, sc.calculated_at
  from account_scores sc
  where sc.account_id = a.id
  order by sc.calculated_at desc, sc.id desc
  limit 1
) s on true
-- A safra de fit, representada pela linha DE ENTRADA quando ela existe.
--
-- As tres clausulas de ordenacao sao tres decisoes, e nenhuma e cosmetica:
--
--   calculated_at desc   a safra mais nova;
--   recommended_entry    dentro dela, a linha da porta de entrada - que e a que
--                        `has_recommended_entry` reporta e a que as personas da
--                        busca de contatos saem;
--   id desc              desempate final.
--
-- O desempate NAO e zelo excessivo. `product_fit.calculated_at` tem default
-- `now()`, que no Postgres e o inicio da TRANSACAO: as cinco linhas de uma safra
-- carregam o mesmo timestamp ao microssegundo. Um `order by calculated_at desc
-- limit 1` sem desempate deixa a escolha para o planner, e `product_fit_batch_id`
-- passa a oscilar entre leituras da mesma safra.
--
-- Isso corroeria em silencio a chave `contacts:{conta}:{safra}`, cujo proposito
-- e justamente "uma busca por safra de fit": id oscilando e chave nova, e a
-- chamada de modelo que a chave existe para evitar acontece assim mesmo.
left join lateral (
  select pf.id, pf.calculated_at, pf.recommended_entry
  from product_fit pf
  where pf.account_id = a.id
  order by pf.calculated_at desc, pf.recommended_entry desc, pf.id desc
  limit 1
) f on true;

comment on view v_account_progress is
  'Retrato completo para AccountOrchestration.Decide. Uma leitura, um instante.';

-- ---------------------------------------------------------------------------
-- 6. RLS - a 0009 ligou em tudo que existia naquele momento
-- ---------------------------------------------------------------------------

alter table product_fit_evidence enable row level security;
alter table contact_evidence     enable row level security;
