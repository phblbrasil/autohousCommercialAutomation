# Modelo de dados

Esquema P0 completo, em SQL puro sob `database/migrations/`. Scripts são
**imutáveis**: corrigir significa criar um script novo numerado, nunca editar um já
aplicado (foi assim que `0010` nasceu).

## Correções aplicadas sobre o SQL do blueprint

| # | Lacuna no §8 | Correção | Onde |
|---|---|---|---|
| 1 | `updated_at` nunca era atualizado | função `set_updated_at()` + triggers | `0009` |
| 2 | `contacts` sem unicidade — People Finder acumularia duplicatas | unique index parcial em `(account_id, normalized_name, coalesce(job_title,''))` | `0003` |
| 3 | `product_fit` / `account_scores` append-only sem ponteiro de "atual" | views `v_account_current_score`, `v_account_current_fit` | `0009` |
| 4 | Chave de idempotência mensal bloquearia retry após falha | chave por execução (`research:{account}:{run}`); cooldown vira regra da API | `0006` |
| 5 | Nenhuma RLS | `enable row level security` em todas as tabelas, sem policies | `0009` |
| 6 | `account_locations` sem unicidade — repesquisa duplicaria lojas | unique index em `(account_id, coalesce(name,''), coalesce(city,''))` | `0010` |
| 7 | Nenhum estágio de dado cru — regra de normalização errada destruía a origem | `companies_raw` com payload `jsonb` e lineage por linha | `0012` |
| 8 | Nenhuma fila de revisão para o agrupamento — o quality gate de confiança não tinha onde parar | `account_merge_candidates` | `0012` |

## Dois ciclos de vida, não um

O diagrama do §18 funde dois ciclos distintos. Aqui eles são separados:

- **`accounts.status`** — `discovered → researching → researched → scored → ready →
  contacted → engaged → customer`, mais `nurture`, `suppressed`, `rejected`.
- **`opportunities.stage`** — `meeting → sql → discovery → proposal → negotiation →
  won | lost`.

Uma conta pode ter várias oportunidades simultâneas em estágios diferentes; o estado
da conta não comporta isso.

## Captura e account graph (migration 0012)

Três tabelas fecham as etapas 01–03 do pipeline do frame 04 da V2:

| Tabela | Papel |
|---|---|
| `ingestion_batches` | unidade de lote, com totais e lineage da fonte |
| `companies_raw` | a linha exatamente como chegou, antes de qualquer interpretação |
| `account_merge_candidates` | fila de revisão humana do agrupamento |

`companies_raw_batch_hash_uq` sobre `(batch_id, content_hash)` faz reimportar o
mesmo arquivo ser no-op em vez de erro. O hash deriva dos campos, não do JSON
serializado: reordenar colunas no CSV de origem não inventa linhas novas.

`account_merge_candidates_pending_uq` é índice **parcial**
(`where status = 'pending'`) — o mesmo CNPJ não pode gerar duas entradas pendentes
para a mesma conta, mas pode reaparecer depois de uma decisão. Como todo índice
parcial neste schema, o `ON CONFLICT` correspondente repete o predicado.

Detalhes do pipeline em [ingestion.md](ingestion.md); as decisões em
[ADR-0004](adr/0004-linha-crua-antes-da-normalizacao.md) e
[ADR-0006](adr/0006-fila-de-revisao-de-merge.md).

## Fonte oficial da Receita (migrations 0013 e 0014)

| Tabela | Papel |
|---|---|
| `receita_releases` | que competência gerou que carga, com SHA-256 de cada zip |
| `rf_cnae_stats` | agregado por `release × uf × cnae × situação × matriz/filial`, **todos** os CNAEs |
| `rf_municipio_stats` | mesmo agregado com granularidade de município, só o universo do catálogo |
| `company_partners` | quadro societário — **PII**, migration própria, opt-in |

Três decisões de schema que não são óbvias:

**Colunas de chave do agregado são `not null`, e "não informado" é a string
vazia.** Chave primária não aceita `NULL`, e a Receita deixa UF em branco para
estabelecimento no exterior. Um sentinela inventado (`'??'`) exigiria que todo
consumidor conhecesse a convenção; a string vazia é o próprio dado ausente.

**Duas tabelas de agregado, e não uma com `municipio` anulável.** Misturadas, todo
`sum()` teria de lembrar de filtrar `municipio is null` para não contar em dobro —
e um dia alguém esquece.

**`company_partners` sozinha na `0014`.** É a única tabela com PII de pessoa
física; separá-la faz de "parar de guardar isso" um `drop table` em vez de um
projeto. Ver [ADR-0008](adr/0008-socios-e-pii.md).

`companies_cnpj` ganhou na `0013` as colunas que a fonte oficial preenche e
nenhuma outra preenchia: `capital_social`, `matriz_filial`, `municipio_codigo`,
endereço, telefone, e-mail, `opcao_simples`/`opcao_mei` e a data/motivo da
situação cadastral. Junto com elas, `natureza_juridica`, `porte`, `data_abertura`
e `cnaes_secundarios` — que existiam desde a `0002` e nasceram vazias por falta de
fonte — passaram a ter valor.

### O índice que faltava

```sql
create index companies_cnpj_root_idx on companies_cnpj (left(cnpj, 8));
```

`AccountGraphRepository.FindCandidatesAsync` filtra por `left(c.cnpj, 8)` para
achar matriz e filial do mesmo grupo, e não havia índice sobre essa expressão. Com
as dezenas de CNPJs do piloto, irrelevante. Com as ~700 mil linhas de uma carga
nacional, um seq scan **por linha do lote** sobre uma tabela que cresce durante a
própria carga — custo quadrático, na prática infinito.

## Dívidas conscientes

- **`website_audits.evidence_ids uuid[]`** — array sem integridade referencial.
  Aceito por ora; vira tabela de ligação quando o Website Auditor for implementado.
- **`sources.content_hash`** — hoje é o SHA-256 da URL normalizada, não do conteúdo
  buscado. Quando armazenarmos o corpo da página, o hash passa a ser do conteúdo e a
  identidade do documento fica correta mesmo com URL variável.
- **`technologies` não existe.** É P1 no frame 03 da V2 e entra junto com o
  Website Auditor, que é quem a preencheria.
- **`companies_raw` cresce sem política de retenção.** Com o filtro de origem do
  [ADR-0007](adr/0007-filtro-de-cnae-na-origem.md) a carga nacional grava ~700 mil
  linhas por release, e não os ~63 milhões — mas doze recargas mensais ainda são
  8 milhões de linhas de `jsonb`. Vira particionamento por lote ou expurgo das
  competências antigas. Gatilho registrado no ADR-0004.
- **`company_partners` sem política de retenção**, como `contacts` e
  `contact_channels`. É pré-requisito do People Finder (A05); o opt-in existe para
  que a base não acumule PII antes de a política existir.
- **Porte fora do agregado nacional.** Ele vive no arquivo `Empresas`, e cruzá-lo
  com `Estabelecimentos` custaria uma passada extra sobre ~63 milhões de linhas.
  Para o universo automotivo ele está em `companies_cnpj.porte`; para o mercado
  inteiro, não existe.

## Materialização com Dapper

Records **posicionais** exigem construtor com assinatura exata e falham em tempo
de execução — não de compilação — quando um tipo diverge. `timestamptz` volta do
Npgsql como `DateTime`, não `DateTimeOffset`, então:

```csharp
// quebra em runtime contra timestamptz
private sealed record SignalRow(string SignalType, decimal Strength, DateTimeOffset ObservedAt);

// materializa por nome de propriedade, com a conversão explícita acima
private sealed record SignalRow
{
    public string SignalType { get; init; } = string.Empty;
    public decimal Strength { get; init; }
    public DateTime ObservedAt { get; init; }
}
```

Linhas de leitura usam propriedades.

## Armadilha de `ON CONFLICT`

`sources_content_hash_uq` e `contacts_identity_uq` são índices **parciais**. A
inferência de `ON CONFLICT` sobre índice parcial exige repetir o predicado no comando:

```sql
on conflict (content_hash) where content_hash is not null do nothing
```

Sem o `where`, o Postgres devolve `42P10: there is no unique or exclusion constraint
matching the ON CONFLICT specification`.

## Busca full-text (migration 0011)

Duas ferramentas, para dois problemas que costumam ser confundidos:

| Pergunta | Ferramenta | Onde |
|---|---|---|
| "quais contas **falam** sobre expansão?" | full-text search | `search_vector` + GIN em `accounts`, `evidence`, `signals` |
| "qual razão social **se parece** com esta?" | similaridade de trigrama | `pg_trgm` em `normalized_name`, `razao_social`, `nome_fantasia` |

Usar FTS para casar nome de empresa daria resultado ruim: stemmer e stopwords
existem para prosa, não para razão social. Por isso o account graph (§11) usa
trigrama.

### Configuração `portuguese_unaccent`

Criada como cópia de `portuguese` com o dicionário `unaccent` antes do stemmer.
Existe por uma restrição concreta: `unaccent()` é apenas `STABLE` (depende de
dicionário em disco) e por isso **não pode aparecer em coluna gerada**. Já
`to_tsvector(regconfig, texto)`, com a configuração fixa, é `IMMUTABLE` — e é isso
que torna a coluna gerada legal.

Efeito prático: "veiculos" encontra "veículos", "sao paulo" encontra "São Paulo".

### Pesos e ranking

As colunas usam `setweight`: nome da conta é `A`, domínio `B`, cidade `C`, segmento
`D`. `ts_rank_cd` respeita a hierarquia, então uma conta chamada "Bauru Motors"
ranqueia acima de uma conta apenas *localizada* em Bauru.

### Limitação conhecida do stemmer — e a compensação

O Snowball português gera stems **diferentes** para substantivo e verbo da mesma
família:

```
expansão    -> 'expansa'
expandindo  -> 'expand'
```

Quem busca "expansao" não encontraria "o grupo está expandindo" — exatamente o
sinal comercial que mais interessa. Plurais o stemmer resolve sozinho
(`lojas` → `loj`).

A solução canônica seria um dicionário de sinônimos do próprio Postgres, mas ele
exige arquivo em `$SHAREDIR/tsearch_data` no servidor — inviável em Postgres
gerenciado como o Supabase. Por isso a expansão acontece na aplicação, em
`SearchQueryExpander`, com um dicionário do domínio automotivo/comercial.

### Sintaxe aceita

`websearch_to_tsquery` entende o que o usuário já conhece de buscadores:

```
"nova unidade"        frase exata
expansao OR aquisicao alternativa
unidades -jornal      exclusão
```

## O que a 0017 acrescentou — Product Matcher, People Finder e Orchestrator

As três tabelas centrais desta etapa já existiam: `product_fit` e `account_scores`
desde a `0005`, `contacts` e `contact_channels` desde a `0003` — esta última com a
nota de que "quem popula é o People Finder". O que faltava não era tabela.

**Lastro.** Nem `product_fit` nem `contacts` sabiam apontar para as evidências que
os sustentam: a Regra 1 valia só para pesquisa e auditoria. Entram
`product_fit_evidence` e `contact_evidence`, tabelas de ligação — e não colunas
`uuid[]`, porque a `0015` já pagou o preço de descobrir que array não tem
integridade referencial.

`contact_evidence` carrega `claim_scope`, que as outras ligações não têm.
`'identity'` é "esta pessoa ocupa este cargo aqui"; o nome do canal (`'email'`,
`'phone'`) é "este canal é dela". A separação existe porque achar o nome e achar o
e-mail são duas descobertas, e o `EvidenceFirstGuard` recusa o run quando o canal
reaproveita a evidência da identidade — é assim que se detecta endereço deduzido do
padrão da empresa.

**Rastreabilidade de execução.** `research_run_id` e `agent_run_id` nas duas
tabelas, fechando a mesma lacuna que a `0015` fechou em `website_audits`.

**Pisos de PII no banco.** `contacts.confidence >= 0.5` e
`contact_channels.confidence >= 0.6`, por `check`. O guard já recusa o run antes
disso; o check cobre o caminho que o guard não vê — escrita manual, script de
correção, carga.

**`product_fit.reasons` mudou de forma.** Era um array de texto; agora é um objeto
com `angle` (a frase do agente), `criteria` (a aritmética determinística) e
`narrative` (os motivos com `evidence_id`). As duas metades ficam separadas porque
têm naturezas diferentes: a aritmética precisa ter resposta mesmo quando o agente
falha. Safras anteriores à `0017` guardam o formato antigo, e o comentário da
coluna registra isso.

**Desqualificador é sinal negativo, não tabela.** Recuperação judicial,
encerramento e mudança de ramo viram linha em `signals` com `strength` negativa —
é um fato datado com evidência sobre a conta, que é exatamente o que `signals`
guarda. O check de `strength` foi aberto para `[-1, 1]`, e `-1` é o valor que a
view lê como bloqueio.

**`website_audits.portal_count`.** A `0015` gravava `multiple_portals boolean`,
derivado de `portals[]` da saída do agente — e o array em si não sobrevivia à
persistência, ficando só dentro de `research_runs.result`, que é JSON de auditoria
e não de consulta. Bastava para o Opportunity Score, que só pergunta "há
fragmentação?"; não basta para o fit de MotorHub, que separa dois canais de
quatro. `NULL` continua sendo "não verificado", e `0` é "verificado e não há" — a
mesma distinção que separa `false` de `null` no resto da auditoria.

**`v_account_progress`.** O retrato completo para `AccountOrchestration.Decide`,
numa leitura só. View e não seis chamadas a repositórios por uma razão de correção,
não de desempenho: a decisão é função do retrato inteiro, e seis leituras
independentes veem seis instantes — uma auditoria concluindo entre a terceira e a
quarta faria a decisão sair sobre um estado que nunca existiu.

Duas colunas dela merecem nota, porque são o que impede laço no Orchestrator:

- `last_audited_at` marca a auditoria **tentada**, não a bem-sucedida. Site fora do
  ar produz linha em `website_audits` de propósito, e é isso que impede um domínio
  morto de pedir auditoria a cada evento.
- `contacts_searched_at` responde "já procuramos?", e não "temos contatos?". Uma
  busca que voltou vazia é um resultado; a pergunta errada faria a conta sem
  ninguém localizável refazer a mesma busca para sempre.


---

## O que a 0018 acrescentou — a auditoria que o motor de IA lê

A `0015` respondia *"o site está bem feito?"*. Para quem vende carro a pergunta
virou outra, em duas frentes que aquele conjunto não alcançava.

**GEO — o motor generativo consegue LER?** `ai_crawlers_blocked text[]` guarda os
rastreadores de IA que o `robots.txt` recusa, e `ai_search_crawlers_blocked`
conta quantos deles respondem a pergunta do comprador **agora**, em vez de
coletar para treino futuro. A distinção não é preciosismo: recusar `CCBot` é
decisão legítima de muita empresa; recusar `OAI-SearchBot` tira a loja do
resultado que alguém vê enquanto pergunta onde achar o carro — e quase sempre
ninguém decidiu isso.

Array e não tabela de ligação, ao contrário de `website_audit_evidence`: aqui não
há integridade referencial a proteger, são nomes de user-agent lidos de um
arquivo de texto, sem entidade correspondente no banco.

`raw_text_words` é o número que denuncia a vitrine em SPA — texto visível no HTML
cru, sem executar JavaScript, que é exatamente o que o rastreador vê. Home de
concessionária com 40 palavras não tem "pouco conteúdo": tem o estoque inteiro
atrás de uma chamada que aquele rastreador não faz.

**AEO — o motor consegue ENTENDER?** `structured_data_types text[]` substitui na
prática o booleano `has_structured_data`, que não distinguia um rodapé com
`Organization` de uma vitrine marcada com `Vehicle` e `Offer` — e é a segunda que
faz o estoque ser citável. `structured_data_has_nap` verifica nome, endereço e
telefone juntos: o mínimo para um motor afirmar QUAL negócio é aquele.

**Qualidade.** Comprimento de título e descrição (presença já era medida;
comprimento diz se sobrevive ao corte do resultado), `canonical_self_referencing`
— canonical errado é pior que ausente, porque o buscador obedece e tira a página
do índice —, e as imagens por `alt`, dimensão declarada (o proxy de CLS que dá
para medir sem navegador) e formato moderno.

## O que a 0019 e a 0020 acrescentaram — a base de calibração

`probe_samples` existe para o [ADR-0013](adr/0013-severidade-sai-da-populacao.md):
a severidade de um achado sai do percentil do segmento, e não de constante
inventada. Para isso é preciso conhecer a distribuição do mercado.

**Não é `website_audits`, e a separação é a decisão que importa.** Auditoria
pertence a uma conta, alimenta Technology Pain e sustenta uma abordagem
comercial. Amostra de calibração é medição de população sobre um domínio
**adivinhado** a partir de `companies_cnpj.email` — que pode nem ser o site da
empresa. Misturar as duas daria à conta uma auditoria que ninguém pediu, com
domínio não confirmado, e enviesaria a distribuição com as contas que já passaram
pelo Researcher: justamente as menos representativas, porque foram escolhidas a
dedo.

`v_probe_distribution` calcula a distribuição por estrato. A `0020` a refez para
mostrar o **denominador** de cada percentual ao lado do numerador: `avg` ignora
`NULL`, então site sem `robots.txt` legível sai da conta, e o `n` do estrato não
era esse subconjunto. Percentual com denominador escondido convida à divisão
errada — a mesma falha que o ADR-0013 tentava evitar ao exigir `n` visível.
