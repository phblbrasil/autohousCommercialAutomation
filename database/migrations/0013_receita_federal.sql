-- 0013 | Dados Abertos CNPJ da Receita Federal como fonte primaria do seed.
--
-- Ate aqui a entrada era um CSV achatado por alguem, fora do sistema. Esta
-- migration da lugar as tres coisas que a fonte oficial exige:
--
--   receita_releases     -- que lote da RF gerou que carga, com SHA-256
--   rf_cnae_stats        -- o agregado de mercado sobre a base INTEIRA
--   rf_municipio_stats   -- a concentracao geografica do universo automotivo
--
-- ...mais as colunas de companies_cnpj que a RF preenche e ninguem preenchia, e
-- um indice sem o qual a resolucao do grafo em escala nao termina.

-- 1. Lineage da fonte -----------------------------------------------------
-- O zip da RF e a fonte duravel desta camada: o filtro de CNAE acontece no
-- stream, entao companies_raw nao guarda tudo o que foi lido. Guardar release e
-- SHA-256 e o que torna a carga reproduzivel mesmo assim - reimportar o mesmo
-- release da o mesmo resultado, o que um extrato ad-hoc nunca garantiu.
create table receita_releases (
  id uuid primary key default gen_random_uuid(),
  -- Competencia publicada pela RF, no formato AAAA-MM.
  release text not null,
  source_uri text,
  -- [{name, length, sha256}] por arquivo baixado.
  files jsonb not null default '[]'::jsonb,
  -- downloading -> downloaded -> streamed -> loaded | failed
  status text not null default 'downloading',
  -- Contagens da passada de Estabelecimentos, ANTES do filtro de CNAE.
  establishments_scanned bigint not null default 0,
  establishments_selected bigint not null default 0,
  companies_joined bigint not null default 0,
  partners_loaded bigint not null default 0,
  batch_id uuid references ingestion_batches(id) on delete set null,
  notes text,
  started_at timestamptz not null default now(),
  finished_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

-- Uma carga por release. Recarregar o mesmo mes atualiza a linha em vez de
-- acumular historico duplicado do mesmo dado.
create unique index receita_releases_release_uq on receita_releases(release);
create index receita_releases_status_idx on receita_releases(status, started_at desc);

-- 2. Agregado de mercado --------------------------------------------------
-- Contado sobre TODOS os ~63M estabelecimentos, antes de qualquer filtro. E o
-- que impede o filtro de CNAE de esconder o que descartou: o que nao entra em
-- companies_raw continua contado aqui, por UF, CNAE e situacao.
--
-- Porte fica FORA da grade de proposito. Ele vive no arquivo Empresas, nao no
-- Estabelecimentos, e cruzar os dois custaria uma passada extra sobre 63M
-- linhas. Para o universo automotivo o porte ja fica em companies_cnpj.porte.
--
-- Toda coluna de chave e NOT NULL, e "nao informado na fonte" e a string vazia -
-- nao NULL e nao um sentinela inventado. Chave primaria nao aceita NULL, e a RF
-- deixa UF em branco (estabelecimento no exterior) e situacao em branco em uma
-- fracao das linhas; sem esta regra essas linhas sumiriam da contagem, que e
-- justamente o que este agregado existe para impedir.
create table rf_cnae_stats (
  release text not null,
  uf text not null,
  cnae text not null,
  situacao_cadastral text not null,
  -- 1 = matriz, 2 = filial. Separar e o que distingue "300 empresas" de
  -- "300 lojas de 40 grupos".
  matriz_filial text not null,
  establishments bigint not null,
  created_at timestamptz not null default now(),
  primary key (release, uf, cnae, situacao_cadastral, matriz_filial)
);

create index rf_cnae_stats_cnae_idx on rf_cnae_stats(release, cnae);

-- Granularidade de municipio em tabela SEPARADA, e nao como coluna anulavel da
-- anterior. Misturar as duas granularidades obrigaria todo sum() a lembrar de
-- filtrar "municipio is null" para nao contar em dobro - e um dia alguem
-- esquece. Restrita aos CNAEs do CnaeCatalog: a grade completa seria
-- 5.572 municipios x ~1.350 CNAEs.
create table rf_municipio_stats (
  release text not null,
  uf text not null,
  -- Codigo PROPRIO da RF (4 digitos), nao IBGE. O nome vem do join com
  -- Municipios.zip e e desnormalizado aqui porque o codigo sozinho nao e
  -- legivel em nenhum relatorio.
  municipio_codigo text not null,
  municipio_nome text,
  cnae text not null,
  situacao_cadastral text not null,
  establishments bigint not null,
  created_at timestamptz not null default now(),
  -- uf fica fora da chave: o municipio ja a determina.
  primary key (release, municipio_codigo, cnae, situacao_cadastral)
);

create index rf_municipio_stats_uf_idx on rf_municipio_stats(release, uf, cnae);

-- 3. O que a RF preenche e companies_cnpj nao tinha onde guardar -----------
-- natureza_juridica, porte, data_abertura e cnaes_secundarios ja existiam na
-- 0002 e nasceram vazios: nao havia fonte que os trouxesse. Agora ha.
alter table companies_cnpj
  add column capital_social numeric(18,2),
  -- 1 = matriz, 2 = filial. Redundante com o CNPJ (digitos 9-12 = 0001), mas
  -- indexavel - e "quantas filiais tem esta conta?" e consulta de rotina.
  add column matriz_filial text,
  add column municipio_codigo text,
  add column cep text,
  add column logradouro text,
  add column numero text,
  add column complemento text,
  add column bairro text,
  -- Telefone e e-mail DA PESSOA JURIDICA, publicos por natureza. Contato de
  -- pessoa fisica e outra coisa e mora em contacts, sob a politica do frame 09.
  add column telefone_1 text,
  add column telefone_2 text,
  add column email text,
  -- Opcao pelo Simples/MEI: sinal de porte real melhor que o campo porte da RF,
  -- que e auto-declarado e frequentemente "00".
  add column opcao_simples text,
  add column opcao_mei text,
  add column data_situacao_cadastral date,
  add column motivo_situacao_cadastral text;

create index companies_cnpj_porte_idx on companies_cnpj(porte) where porte is not null;
create index companies_cnpj_municipio_codigo_idx on companies_cnpj(municipio_codigo);

-- 4. O indice que separa "horas" de "nunca termina" -----------------------
-- AccountGraphRepository.FindCandidatesAsync filtra por left(c.cnpj, 8) para
-- achar matriz e filial do mesmo grupo. Nao havia indice sobre essa expressao.
--
-- Com as dezenas de CNPJs de teste, irrelevante. Com as ~700 mil linhas do
-- universo automotivo do pais, e um seq scan POR LINHA DO LOTE sobre uma tabela
-- que cresce durante a propria carga - custo quadratico, na pratica infinito.
create index companies_cnpj_root_idx on companies_cnpj (left(cnpj, 8));

-- 5. Triggers e RLS, seguindo a convencao da 0009 -------------------------
create trigger receita_releases_set_updated_at before update on receita_releases
  for each row execute function set_updated_at();

do $$
declare t text;
begin
  foreach t in array array['receita_releases','rf_cnae_stats','rf_municipio_stats'] loop
    execute format('alter table %I enable row level security', t);
  end loop;
end $$;
