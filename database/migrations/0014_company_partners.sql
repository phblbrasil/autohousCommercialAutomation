-- 0014 | Quadro societario (arquivo Socios da RF).
--
-- Migration SEPARADA da 0013 de proposito, e nao por organizacao.
--
-- Todo o resto do banco guarda dado cadastral de pessoa juridica, publico por
-- natureza. Esta tabela guarda PII de pessoa fisica: nome de socio, CPF
-- mascarado, faixa etaria, representante legal. Isso muda a classificacao de
-- risco da base inteira e cai sob o guardrail de minimizacao de PII do frame 09.
--
-- Isolar significa que a decisao e reversivel: se a politica de retencao mudar,
-- "drop table company_partners" devolve a base ao estado anterior sem tocar em
-- account graph, evidencia ou score. Se isto morasse na 0013, desfazer exigiria
-- uma migration nova para cada coluna.
--
-- A carga e opt-in: o CLI so popula esta tabela com --socios.

create table company_partners (
  id uuid primary key default gen_random_uuid(),
  -- Raiz de oito digitos. O arquivo Socios da RF nao traz o CNPJ completo:
  -- socio e da empresa, nao do estabelecimento.
  cnpj_basico char(8) not null,
  -- 1 = pessoa juridica, 2 = pessoa fisica, 3 = estrangeiro.
  identificador text,
  nome text,
  -- A RF ja entrega mascarado (art. 129 §2o da Lei 13.473/2017): os tres
  -- primeiros digitos e os dois verificadores ocultos. Guardamos como veio -
  -- desmascarar nao e possivel e nao deve parecer possivel.
  cpf_cnpj_mascarado text,
  qualificacao text,
  data_entrada date,
  pais text,
  representante_cpf text,
  representante_nome text,
  representante_qualificacao text,
  faixa_etaria text,
  release text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index company_partners_cnpj_idx on company_partners(cnpj_basico);

-- Identidade do socio dentro da empresa. Sem ela a recarga mensal duplica o
-- quadro societario inteiro a cada execucao - o mesmo defeito que a 0003
-- corrigiu em contacts e a 0010 em account_locations.
create unique index company_partners_identity_uq
  on company_partners(cnpj_basico, coalesce(cpf_cnpj_mascarado, ''), coalesce(nome, ''));

create trigger company_partners_set_updated_at before update on company_partners
  for each row execute function set_updated_at();

alter table company_partners enable row level security;
