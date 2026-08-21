-- 0009 | Tres lacunas do SQL original fechadas: updated_at nunca atualizado,
-- ausencia de ponteiro para o score/fit vigente, e ausencia de RLS.

-- 1. updated_at automatico ------------------------------------------------
create or replace function set_updated_at() returns trigger as $$
begin
  new.updated_at = now();
  return new;
end;
$$ language plpgsql;

do $$
declare t text;
begin
  foreach t in array array['accounts','companies_cnpj','contacts'] loop
    execute format(
      'create trigger %I_set_updated_at before update on %I
       for each row execute function set_updated_at()', t, t);
  end loop;
end $$;

-- 2. Estado vigente -------------------------------------------------------
-- product_fit e account_scores sao append-only. Sem estas views, toda leitura
-- teria que reimplementar a regra de "qual e o score atual".
create view v_account_current_score as
select distinct on (account_id) *
from account_scores
order by account_id, calculated_at desc;

create view v_account_current_fit as
select distinct on (account_id, product) *
from product_fit
order by account_id, product, calculated_at desc;

-- 3. RLS ------------------------------------------------------------------
-- Habilitar sem policies nega por padrao para anon/authenticated, enquanto o
-- service_role do Supabase faz bypass. Torna a migracao para o Supabase segura
-- sem retrabalho (secao 27). Policies de leitura para o dashboard entram quando
-- o dashboard existir.
do $$
declare t text;
begin
  for t in
    select tablename from pg_tables
    where schemaname = 'public' and tablename <> 'schema_versions'
  loop
    execute format('alter table %I enable row level security', t);
  end loop;
end $$;
