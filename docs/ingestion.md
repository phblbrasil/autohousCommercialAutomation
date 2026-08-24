# Captura de dados empresariais

Etapas 01–03 do pipeline do frame 04 da V2: **Seed → Normalize → Account Graph**.

Duas portas de entrada, um pipeline:

| Fonte | Quando | Comando |
|---|---|---|
| **Dados Abertos CNPJ da Receita Federal** | a fonte primária, mensal | `receita --release AAAA-MM` |
| extrato delimitado de terceiro | lista pequena, fonte pontual | `arquivo --file x.csv` |

As duas divergem na **leitura** e em nada depois dela: mesma `companies_raw`,
mesmo `CompanyNormalizer`, mesmo `AccountGroupResolver`. É o que impede a fonte
nova de agrupar contas por uma regra diferente da fonte antiga.

```
fonte (release da Receita  ou  arquivo delimitado)
   │
   ├── captura ────────────────────────────────────────
   │   ingestion_batches (open)
   │   companies_raw     (pending)   ← linha crua, sem interpretação
   │   ingestion_batches (captured)
   │
   ├── por linha, uma transação ──────────────────────
   │   CompanyNormalizer         CNPJ · CNAE · situação · UF
   │      ├─ rejeita  → companies_raw (rejected, motivo)
   │      └─ aceita   → AccountGroupResolver
   │                       ├─ raiz de CNPJ igual   → anexa       (auto)
   │                       ├─ nome ≥ 0.90 + mesma UF → anexa     (auto)
   │                       ├─ nome ≥ 0.75          → fila de revisão
   │                       └─ nada plausível        → conta nova
   │
   └── ingestion_batches (resolved, com os totais)
```

## Por que a linha crua existe

`companies_raw` guarda o registro **exatamente como chegou**, antes de qualquer
validação. A captura não valida CNPJ, não filtra CNAE e não decide grupo.

A normalização vai errar em algum momento: um CNAE novo, um município grafado
diferente, um encoding inesperado, uma coluna trocada. Se a linha original não
estiver no banco, corrigir a regra exige reimportar a fonte — e bases
empresariais mudam entre uma carga e outra, então o dado de ontem não volta. Com
a linha crua guardada, corrigir é reprocessar.

`CompanyNormalizer` é uma função pura pelo mesmo motivo: reprocessar
`companies_raw` só faz sentido se a mesma entrada sempre produzir a mesma saída.

## Motivos de rejeição

Cada linha rejeitada grava o porquê em `companies_raw.rejection_reason`:

| Motivo | Significado |
|---|---|
| `invalid_cnpj` | dígitos verificadores não fecham |
| `missing_name` | sem razão social e sem nome fantasia |
| `unknown_cnae` | código ilegível — provável defeito de parsing |
| `outside_universe` | código válido, fora do catálogo AutoHous |
| `inactive_registration` | situação cadastral diferente de ativa |
| `invalid_uf` | UF que não existe |

`unknown_cnae` e `outside_universe` são deliberadamente distintos. O primeiro é
defeito de leitura do arquivo e merece investigação; o segundo é o filtro
funcionando. Colapsar os dois esconderia uma coluna mal mapeada atrás de "não é
nosso ICP" — e um lote inteiro rejeitado por engano pareceria normal.

## O catálogo de CNAE

`CnaeCatalog` é o primeiro quality gate. Uma base da Receita traz milhões de
empresas; importar tudo para filtrar depois custa armazenamento e polui **todas**
as buscas por similaridade de nome. O filtro acontece na entrada.

O catálogo distingue duas coisas:

- **universo** — o CNAE está no catálogo, e a empresa entra na base;
- **camada de ICP** (`IcpTier`) — em que fila ela entra.

São três camadas, e os números são da competência `2026-08`, contados sobre a
base inteira antes de qualquer filtro (`rf_cnae_stats`):

| Camada | O que é | Ativos | CNAEs |
|---|---|---|---|
| `Core` | quem vende veículo: concessionária, revenda, atacado, intermediação, motos | **94.047** | 4511101-04, 4512901-02, 4541203-04 |
| `Aftermarket` | quem vive de manutenção e peça: oficina mecânica, funilaria, autopeças | **593.022** | 4520001, 4520002, 4530701, 4530703 |
| `Adjacent` | lavagem e polimento, locadora, atacado de reboques, ônibus e motos | **152.340** | 4520005, 7711000, 4511105-06, 4541201, 4542101 |

Era um booleano — dentro ou fora do ICP central — e o booleano escondia a
diferença que mais importava no que ficava de fora: o aftermarket é **6× o ICP
central** em número de estabelecimentos, com motion próprio (ticket menor, compra
menos sobre vitrine de estoque e mais sobre atendimento e recorrência). Chamar
593 mil empresas de "resto" é uma decisão de produto tomada por omissão.

`Adjacent` não é lixo: é mercado automotivo sem produto AutoHous com encaixe
óbvio **hoje**. Promover qualquer um deles a camada própria é uma linha em
`CnaeCatalog`.

Códigos chegam em pelo menos três grafias (`4511-1/01`, `45.11-1-01`, `4511101`).
`NormalizeCode` reduz tudo a sete dígitos: comparar string crua faria a mesma
empresa cair em ramos diferentes conforme o arquivo de origem.

## Account graph — `Account > CNPJ`

É o princípio de desenho nº 1 da V2 virando código. Prospectar por CNPJ isolado
faz dois SDRs atacarem a mesma matriz por filiais diferentes.

`AccountGroupResolver` é puro: recebe os candidatos já buscados e devolve a
decisão. Quem faz a busca por trigrama é o adaptador (é o Postgres que tem o
índice GIN sobre a base inteira); quem decide é a função — testável com uma lista
em memória.

Ordem das regras:

**1. Raiz de CNPJ (oito primeiros dígitos) — confiança 1.00, anexa.**
Filial e matriz compartilham a raiz por definição da Receita. Não há julgamento a
fazer, nem que o nome não pareça.

**2. Nome ≥ 0.90 e mesma UF — anexa.**
Grupos automotivos são regionais. "Vento Sul Veículos" em SP e em RS são, quase
sempre, empresas diferentes com um nome genérico parecido.

**3. Nome ≥ 0.75 — fila de revisão.**
Um falso merge custa mais que um falso split: unir dois grupos econômicos
distintos faz dois SDRs atacarem a mesma conta com teses erradas, e desfazer
exige reconstruir evidências e histórico. A faixa de revisão é generosa de
propósito.

**4. Abaixo de 0.75 — conta nova, confiança 1.00.**
Confundir "nenhum candidato" com "baixa confiança" encheria a fila de revisão de
conta legítima — o oposto do que a fila serve.

Os limiares vivem em `AccountSimilarity`, no domínio, e são passados como
parâmetro para o SQL. Uma única definição.

## A fila de revisão

`account_merge_candidates` é a review queue do quality gate *"Account confidence
≥ 0.80"*. A linha correspondente fica em `review` e **não vira conta** — deixar a
conta nascer e "consertar depois" é o caminho para duas contas do mesmo grupo
receberem pesquisa paga em paralelo.

```bash
curl -H "Authorization: Bearer $REVENUE_API_KEY" localhost:5080/merge-candidates
curl -X POST localhost:5080/merge-candidates/<ID>/decide \
  -H 'content-type: application/json' -d '{"approve":true,"decidedBy":"pedro"}'
```

**Rejeitar não é descartar.** Se o revisor diz que a empresa não pertence ao
grupo sugerido, ela é uma conta legítima e distinta — e passa a ter conta
própria. Sem isso, a linha revisada e negada sumiria do funil depois de ter
custado revisão humana.

A decisão renormaliza a partir de `companies_raw`, e não dos campos copiados para
a fila: a fila guarda o suficiente para o humano decidir, não o suficiente para
escrever em `companies_cnpj`.

## Recarga é atualização, não no-op

Um CNPJ que já está na base não cria conta nova — mas também não é ignorado. A
recarga mensal traz situação cadastral, nome fantasia e município atualizados, e
a Receita é a autoridade sobre esses campos. `AttachCompanyAsync` é upsert:
reanexar refresca o cadastro sem mexer no vínculo.

Reimportar o mesmo arquivo no mesmo lote, por outro lado, é no-op de verdade —
`companies_raw_batch_hash_uq` garante isso.

## A fonte oficial: Dados Abertos CNPJ

### O que a Receita publica, e onde

O repositório migrou para um Nextcloud (SERPRO+). Os caminhos que circulam na
internet — `/dados/cnpj/dados_abertos_cnpj/` e o antigo `dadosabertos.rfb.gov.br`
— respondem **404** e timeout. O que funciona é WebDAV sobre o compartilhamento
público:

```
https://arquivos.receitafederal.gov.br/            → 302 → /index.php/s/<TOKEN>
PROPFIND /public.php/webdav/Dados/Cadastros/CNPJ/  → 2023-05 … 2026-08
```

Autenticação Basic com o token no lugar do usuário e senha vazia. `Range`
funciona, e isso não é detalhe: `Estabelecimentos0.zip` tem 2 GB, e sem retomada
qualquer queda de conexão reinicia o download do zero.

**O token é descoberto do redirect, não fixado no código.** Ele já mudou uma vez
junto com a plataforma; embutido, a próxima migração derrubaria a carga mensal
sem aviso. `RECEITA_SHARE_TOKEN` sobrepõe quando a descoberta falhar.

### O formato

Nada aqui é o que uma ferramenta moderna produziria, e cada diferença já custou
um defeito em algum projeto:

| Característica | O que quebra se ignorada |
|---|---|
| **sem cabeçalho**, posicional | trocar duas colunas continua produzindo dado com cara de válido |
| **ISO-8859-1** | ler como UTF-8 não falha: corrompe a razão social em silêncio |
| zip com **uma entrada de nome opaco** (`F.K03200$Z.D60808.ESTABELE`) | abrir por nome não funciona; o nome muda a cada release |
| `MUNICIPIO` é **código próprio da RF** de 4 dígitos, não IBGE | `companies_cnpj.municipio` gravaria `"6219"` no lugar de `"Bauru"` |
| capital social com **vírgula decimal** | `"1000,00"` vira cem mil num servidor com locale inglês |
| data como `AAAAMMDD`, "sem data" como `0` ou `00000000` | `01/01/0001` poluindo todo cálculo de idade da empresa |

As posições de cada campo vivem em `ReceitaLayout`, nomeadas, e não espalhadas
como `fields[11]` — conferidas contra `cnpj-metadados.pdf` e contra bytes reais
do release `2026-08`.

### As quatro passadas

A ordem não é escolha: razão social vive em `Empresas`, chaveada pela raiz do
CNPJ, e só depois de varrer os 5,1 GB de estabelecimentos se sabe quais raízes
interessam.

```
A. Estabelecimentos  5,1 GB   conta TUDO para o agregado
                              guarda no spool o universo automotivo
B. Empresas          1,3 GB   razão social, porte, capital — só das raízes de A
C. Simples           288 MB   opção pelo Simples e MEI, mesmo recorte
D. Socios            650 MB   quadro societário, mesmo recorte, só com --socios
                              ↓
   spool (matriz, depois filial) + B + C  →  RawCompanyRow  →  companies_raw
```

**O spool existe por causa dessa ordem.** Sem ele restariam duas saídas ruins:
segurar centenas de milhares de estabelecimentos em memória durante a passada B,
ou reler os 5,1 GB. Efeito colateral útil: refazer a junção depois de corrigir um
mapeamento não exige baixar nem reler a fonte.

**Matriz antes de filial**, em dois spools separados. A regra 1 do
`AccountGroupResolver` é raiz de CNPJ: com a matriz já na base, a filial anexa por
identidade, confiança 1.00. Na ordem inversa, a filial cria a conta e a matriz
chega depois disputando trigrama contra o nome da própria filial.

### O filtro na origem

Dos 72,8 milhões de estabelecimentos da competência `2026-08`, o universo do
`CnaeCatalog` são 839.409 ativos — 1,2%.
Gravar os 63 milhões em `companies_raw` custaria dezenas de GB de `jsonb` e
poluiria **todo** índice trigrama — o `AccountGroupResolver` compara nome contra a
tabela inteira.

O filtro testa três coisas, nenhuma interpretativa: CNAE no catálogo, situação
ativa, UF pedida. Tudo que exige julgamento — dígito verificador, nome ausente,
UF inexistente — continua acontecendo no `CompanyNormalizer`, **depois** que a
linha já está gravada, com `rejection_reason`. Ver
[ADR-0007](adr/0007-filtro-de-cnae-na-origem.md).

**O que o filtro descarta não some:** `rf_cnae_stats` conta cada uma das 63
milhões de linhas antes de qualquer filtro.

### O agregado de mercado

Duas tabelas, duas perguntas:

| Tabela | Recorte | Responde |
|---|---|---|
| `rf_cnae_stats` | `release × uf × cnae × situação × matriz/filial`, **todos** os CNAEs | qual é o TAM, e quanto dele a base cobre |
| `rf_municipio_stats` | `release × município × cnae × situação`, só o catálogo | onde o mercado automotivo se concentra |

Granularidades em tabelas separadas, e não como coluna anulável: misturadas,
todo `sum()` teria de lembrar de filtrar `municipio is null` para não contar em
dobro — e um dia alguém esquece.

Porte fica fora da grade de propósito. Ele vive em `Empresas`, não em
`Estabelecimentos`, e cruzá-lo custaria uma passada extra sobre 63 milhões de
linhas; para o universo automotivo ele já está em `companies_cnpj.porte`.

Colunas de chave são `not null`, e "não informado na fonte" é a **string vazia**:
chave primária não aceita `NULL`, e a Receita deixa UF em branco para
estabelecimento no exterior. Descartar essas linhas seria o oposto do que o
agregado existe para fazer.

### Lineage

`receita_releases` guarda release, status, contagens, o lote gerado e o **SHA-256
de cada zip**. É o que sustenta a promessa de que reimportar o mesmo release dá o
mesmo resultado — a Receita não publica checksum, e o tamanho declarado pelo
`PROPFIND` é a única verificação possível contra a origem.

Uma linha por competência: duas linhas com contagens diferentes para `2026-08`
não teriam como ser desempatadas depois.

## A CLI

```bash
export REVENUE_DB_CONNECTION="Host=localhost;Port=5433;Database=autohous_revenue;Username=revenue;Password=revenue"

# competências publicadas (só PROPFIND, não toca no banco)
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita --list

# só o agregado de mercado, sem capturar empresa nenhuma
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita \
  --release 2026-08 --stats-only

# ensaio: lê e conta, não grava nada
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita \
  --release 2026-08 --dry-run --limit 200000

# carga nacional
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita \
  --release 2026-08 --socios

# zips já baixados por outro meio
dotnet run --project src/AutoHous.Revenue.Ingestor -- receita \
  --release 2026-08 --offline --cache-dir /mnt/dados/cnpj
```

| Flag | Padrão | Efeito |
|---|---|---|
| `--uf SP,PR` | país inteiro | recorte por UF |
| `--incluir-inativos` | desligada | mantém situação cadastral não ativa; ~dobra o volume |
| `--incluir-cnae-secundario` | desligada | admite CNAE do catálogo entre os secundários |
| `--socios` | desligada | carrega o quadro societário — PII, ver [governance.md](governance.md) |
| `--offline` | desligada | usa só o cache; exige `--release` |
| `--keep-spool` | desligada | não apaga o spool ao final |

`--limit` só vale com `--dry-run`: leitura parcial produz agregado parcial, e
gravá-lo como se fosse o mercado seria pior que não ter número nenhum.

**`--dry-run` e `--stats-only` não gravam, mas leem.** Os dois precisam dos
estabelecimentos e das tabelas de domínio — ~5,1 GB, uma vez, e depois em cache.
Nenhum dos dois baixa `Empresas`, `Simples` ou `Socios`: eles não juntam nada, e
garantir 2,2 GB para não ler seria custo puro.

## A CLI de arquivo

```bash
export REVENUE_DB_CONNECTION="Host=localhost;Port=5433;Database=autohous_revenue;Username=revenue;Password=revenue"

# simula: roda a normalização real e não grava nada
dotnet run --project src/AutoHous.Revenue.Ingestor -- \
  --file receita-sp.csv --encoding latin1 --dry-run

# captura e resolve o grafo
dotnet run --project src/AutoHous.Revenue.Ingestor -- \
  --file receita-sp.csv --encoding latin1 --source "receita-2026-08"
```

Colunas são reconhecidas por apelido, sem depender da ordem: `cnpj`,
`razao_social`, `nome_fantasia`, `cnae_principal`, `situacao_cadastral`,
`municipio`, `uf`. Cabeçalhos com acento e caixa alta funcionam. Extratos da base
da Receita circulam com cabeçalhos diferentes conforme a ferramenta que os gerou;
exigir um layout único garantiria retrabalho manual a cada nova fonte.

Colunas não mapeadas geram **aviso**, não erro — colunas extras são normais. Mas
se a que faltou for a de CNAE, o lote inteiro cai em `unknown_cnae`, e o silêncio
custaria uma investigação.

Códigos de saída:

| Código | Significado |
|---|---|
| 0 | lote capturado e grafo resolvido |
| 1 | fonte sem linhas de dados |
| 2 | argumentos inválidos |
| 3 | gravado, mas abaixo do quality gate de 85% de resolução automática |
| 4 | fonte da Receita indisponível, incompleta ou com layout inesperado |

O código 3 existe para que um pipeline de carga não siga adiante sem alguém
olhar. Falhar o gate não invalida o lote — os dados estão lá.

O código 4 separa "a fonte falhou" de "o dado é ruim": download incompleto, zip
com estrutura inesperada e cache sem os arquivos são problemas de origem, e
reexecutar resolve. Confundi-los com o gate de qualidade faria um pipeline tentar
consertar dado que nunca chegou.

## Por que a CLI e não só o endpoint

Um arquivo da Receita tem centenas de milhares de linhas. Mandar isso por HTTP
significaria upload multipart, timeout de gateway e um endpoint segurando o
arquivo inteiro em memória. `POST /ingestion/batches` continua existindo para
listas pequenas, integração programática e teste.

## Transação por linha

`ResolveAccountGraphUseCase` abre uma transação **por empresa**, não uma para o
lote. Um lote de 300 mil CNPJs em transação única seguraria locks por minutos e
perderia todo o trabalho em qualquer erro. A unidade de consistência do negócio
aqui é a empresa, não o arquivo.
