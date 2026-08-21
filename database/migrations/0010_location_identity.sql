-- 0010 | account_locations nascia sem unicidade, entao repesquisar uma conta
-- duplicaria as lojas a cada execucao. Scripts aplicados sao imutaveis: a
-- correcao vem como script novo, nunca editando 0002.

-- Remove duplicatas eventuais antes de criar o indice.
delete from account_locations a
 using account_locations b
 where a.ctid > b.ctid
   and a.account_id = b.account_id
   and coalesce(a.name, '') = coalesce(b.name, '')
   and coalesce(a.city, '') = coalesce(b.city, '');

create unique index account_locations_identity_uq
  on account_locations(account_id, coalesce(name, ''), coalesce(city, ''));
