-- 0018 | Auditoria profunda: AEO, GEO e a qualidade que ranqueia.
--
-- A 0015 criou `website_audits` com o que a sonda media entao: desempenho, SEO
-- de presenca (tem title? tem canonical?), mobile e rastreio. Era uma auditoria
-- que respondia "o site esta bem feito?".
--
-- A pergunta mudou. Para quem vende carro hoje, o site precisa responder duas
-- coisas que aquele conjunto nao alcanca:
--
--   GEO  o motor generativo consegue LER este site? Uma loja que bloqueia
--        OAI-SearchBot no robots.txt nao existe quando o comprador pergunta ao
--        assistente onde achar o carro - e ninguem no negocio sabe, porque o
--        bloqueio quase sempre veio de um tutorial de "proteja seu conteudo".
--
--   AEO  o motor consegue ENTENDER o que esta a venda? "Tem dado estruturado"
--        era booleano e nao distinguia um rodape com Organization de uma vitrine
--        marcada com Vehicle e Offer. E a segunda que torna o estoque citavel.
--
-- Tudo aqui e MEDIDO pela sonda, nunca inferido pelo agente. A divisao do
-- auditor continua a mesma: sonda mede, agente observa com evidencia, plataforma
-- pontua.

-- ---------------------------------------------------------------------------
-- 1. GEO — descoberta por motor generativo
-- ---------------------------------------------------------------------------

alter table website_audits
  -- Agentes de IA bloqueados na raiz pelo robots.txt, como texto[].
  --
  -- Array e nao tabela de ligacao, ao contrario de website_audit_evidence: aqui
  -- nao ha integridade referencial a proteger - sao nomes de user-agent lidos de
  -- um arquivo de texto, sem entidade correspondente no banco. A 0015 trocou
  -- array por tabela quando o array apontava para `evidence`; este nao aponta
  -- para nada.
  add column ai_crawlers_blocked text[],

  -- Destes, quantos respondem a pergunta do comprador AGORA (busca), em vez de
  -- coletar para treino futuro.
  --
  -- Guardado calculado porque e o numero que vira argumento comercial, e a
  -- classificacao de cada agente vive no codigo (AiCrawlers), nao no banco:
  -- recalcular exigiria manter a taxonomia duplicada aqui.
  add column ai_search_crawlers_blocked integer,

  add column has_llms_txt boolean,

  -- NULL = nao verificado; false = declara noindex. A distincao importa porque
  -- um noindex esquecido em migracao zera a aquisicao organica, e o sintoma que
  -- chega ao negocio e "as vendas cairam".
  add column is_indexable boolean,

  -- Palavras de texto visivel no HTML CRU, sem executar JavaScript. E o numero
  -- que denuncia a vitrine em SPA: uma home de concessionaria com 40 palavras
  -- nao tem "pouco conteudo", tem o estoque inteiro atras de uma chamada que o
  -- rastreador nao faz.
  add column raw_text_words integer;

comment on column website_audits.ai_crawlers_blocked is
  'User-agents de IA bloqueados na raiz. NULL = robots.txt nao verificado; '
  'array vazio = verificado e nenhum bloqueado. A distincao e a mesma que separa '
  'false de null no resto da auditoria.';

-- ---------------------------------------------------------------------------
-- 2. AEO — legibilidade por maquina
-- ---------------------------------------------------------------------------

alter table website_audits
  add column structured_data_types text[],
  add column structured_data_has_nap boolean,
  add column h1_count integer,
  add column h2_count integer;

comment on column website_audits.structured_data_types is
  'Valores de @type achados em JSON-LD, incluindo os aninhados em @graph. '
  'A presenca de Vehicle e Offer e o que distingue uma vitrine citavel de um '
  'rodape com Organization - distincao que has_structured_data nao fazia.';

comment on column website_audits.structured_data_has_nap is
  'Nome, endereco e telefone juntos no dado estruturado: o minimo para um motor '
  'de resposta afirmar QUAL negocio e este.';

-- ---------------------------------------------------------------------------
-- 3. Qualidade que ranqueia
-- ---------------------------------------------------------------------------

alter table website_audits
  add column title_length integer,
  add column meta_description_length integer,
  add column canonical_self_referencing boolean,

  add column image_count integer,
  add column images_with_alt integer,
  add column images_with_dimensions integer,
  add column images_modern_format integer,

  add column has_hsts boolean,
  add column internal_link_count integer,
  add column declared_language text;

comment on column website_audits.images_with_dimensions is
  'Imagens que declaram width e height. E o proxy de CLS que da para medir sem '
  'navegador: sem dimensao o layout pula quando a imagem carrega, e num catalogo '
  'de veiculos isso acontece em cada card.';

comment on column website_audits.canonical_self_referencing is
  'NULL = sem canonical. false = aponta para outra URL, que e pior que ausente: '
  'ausente deixa o buscador decidir; errado, ele obedece e tira a pagina do indice.';

-- ---------------------------------------------------------------------------
-- 4. Fila de trabalho: quem esta invisivel para motor de IA
-- ---------------------------------------------------------------------------
-- Indice parcial porque a consulta que importa e sempre a mesma - "quais contas
-- bloqueiam busca de IA?" - e ela olha uma fracao pequena da tabela. Indice
-- cheio custaria manutencao em toda auditoria para servir uma pergunta que so
-- pergunta pelos poucos.

create index website_audits_ai_blocked_idx
  on website_audits(account_id, audited_at desc)
  where ai_search_crawlers_blocked > 0;
