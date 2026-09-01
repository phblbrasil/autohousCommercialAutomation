# Governança

## Regra 1 — evidência

Nenhuma afirmação específica sem fonte. Aplicada mecanicamente pelo
`EvidenceFirstGuard`; violação reprova o run inteiro.

O guard vive no **domínio**, e não junto do validador de schema: é regra de
negócio, não detalhe de biblioteca. O JSON Schema sabe validar "`evidence_index` é
um inteiro ≥ 0"; ele não sabe quantas evidências existem.

O mesmo princípio governa o score: `ScoreComponent.Observed` distingue "olhamos e
não tem" de "ainda não sabemos". Um score de 55 com 40% de cobertura é pedido de
mais pesquisa, não veredito.

## Regra 2 — suppression

Conta em `suppressed` não entra em pesquisa nem em outbound. Verificado no endpoint de
pesquisa e na máquina de estados (`suppressed` é terminal no fluxo automático).

## Regra 3 — cooldown

Deliberadamente **separado** da idempotência:

- **idempotência** identifica uma execução (`research:{account}:{run}`) e impede
  processamento duplicado do mesmo trabalho;
- **cooldown** é regra de negócio: já houve pesquisa concluída neste mês? `409`, salvo
  `?force=true`.

Fundir os dois — como sugere o §19 — impediria o retry de um run que falhou dentro do
mesmo mês.

## Regra 4 — aprovação humana

Todo outbound nasce com `requires_human_approval = true`. Ainda não implementado: não
há outbound nesta entrega.

## Regra 5 — idempotência

Toda ação carrega chave, construída em um único ponto (`IdempotencyKey`). Enfileirar a
mesma chave duas vezes é no-op, não exceção.

## Regra 6 — merge de conta

Unir dois CNPJs em uma conta é irreversível na prática: desfazer exige separar
evidências, sinais, contatos e histórico. Por isso a plataforma só une sozinha
quando há **identidade**, não semelhança:

| Condição | Quem decide |
|---|---|
| mesma raiz de CNPJ | plataforma |
| nome ≥ 0.90 **e** mesma UF | plataforma |
| nome ≥ 0.75 | pessoa, via `/merge-candidates` |
| abaixo disso | plataforma — conta nova |

O agente não participa desta decisão. Ver
[ADR-0006](adr/0006-fila-de-revisao-de-merge.md).

## Minimização de PII

O frame 09 da V2 lista *"PII minimization: armazenar só o necessário"* como
guardrail P0. Estado atual:

| Tabela | Natureza | Situação |
|---|---|---|
| `companies_raw`, `companies_cnpj`, `account_locations` | cadastro de **pessoa jurídica**, público por natureza | sem restrição adicional |
| `company_partners` | **PII de pessoa física** — nome, CPF mascarado, faixa etária | opt-in por execução, sem política de retenção |
| `contacts`, `contact_channels` | **PII de pessoa física** | populadas pelo People Finder (A05), sob as guardas abaixo |

Três observações que mudaram com a camada 01:

**Telefone e e-mail em `companies_cnpj` não são PII de pessoa.** A Receita os
publica como contato do estabelecimento, e é assim que eles são guardados. Esta
camada **não** cria `contacts` a partir deles: contato de pessoa é outra coisa, e
quem popula `contacts` é o People Finder, sob a política do frame 09.

**`company_partners` é opt-in.** A carga só toca `Socios*.zip` com `--socios`;
sem a flag o arquivo nem é baixado. Carregar quadro societário é um ato
deliberado, não efeito colateral da carga mensal. Ver
[ADR-0008](adr/0008-socios-e-pii.md).

**O CPF é guardado como a Receita entrega: mascarado** — os três primeiros
dígitos e os dois verificadores ocultos, por força do art. 129 §2º da Lei
13.473/2017. Não existe caminho de código que o desmascare.

**A política de retenção continua indefinida**, para as três tabelas de PII.

Isso passou a ser uma pendência **ativa**, e não teórica: até a entrega do People
Finder, `contacts` e `contact_channels` estavam vazias e o risco era hipotético.
Agora elas recebem escrita a cada conta do funil.

## O que o People Finder (A05) grava, e o que ele recusa

O único agente que produz PII de pessoa física é também o único com duas camadas
de guarda em vez de uma. Além da Regra 1 — nada sem fonte —, valem as regras de
`ContactPolicy`, impostas em três lugares:

| Regra | Onde é imposta |
|---|---|
| confiança do contato ≥ 0,5 | `EvidenceFirstGuard` (recusa o run) + `check` na `0017` |
| confiança do canal ≥ 0,6 | `EvidenceFirstGuard` + `check` na `0017` |
| cada canal aponta para evidência **diferente** da do contato | `EvidenceFirstGuard` |
| cargo é traduzido pela plataforma, não pelo agente | `PersonaCatalog`, no persister |

O piso é imposto **recusando o run**, e não descartando a linha em silêncio. A
diferença importa: se o modelo está devolvendo palpite, quem precisa saber é quem
lê o erro do run — não um `where confidence >= 0.5` escondido no persister.

**A regra do canal com fonte própria é a que mais rejeita run, e a que mais
protege.** Achar o nome de um diretor numa notícia e achar o e-mail dele são duas
descobertas. Um `nome.sobrenome@empresa.com.br` deduzido do padrão da casa passa
em qualquer schema, tem formato válido e aponta para uma evidência real — a
notícia que citava o nome. Só a regra de escopo o pega. Sem ela, a plataforma
escreveria para um endereço que ninguém nunca viu.

**Provedor pessoal entra marcado, e não bloqueado.** Um Gmail publicado como
contato da empresa é como revenda pequena opera de verdade; descartá-lo deixaria
a conta sem contato nenhum. `contact_channels.is_professional` registra a
distinção, e o e-mail simplesmente não conta como profissional na pontuação.

**O que o agente não procura**, por instrução no prompt versionado e na skill:
endereço residencial, CPF, data de nascimento, estado civil, rede social pessoal,
e qualquer dado de pessoa sem papel de decisão. Nada disso tem campo no contrato
— `additionalProperties: false` fecha a porta que a instrução deixaria entreaberta.

**O que continua sem guarda mecânica:** a origem da fonte. A skill proíbe
agregador de dados pessoais e base vazada, e nenhuma checagem impõe isso — uma URL
de agregador em `evidence[]` passa. É o buraco conhecido desta camada, e o
candidato natural a uma lista de domínios recusados quando houver caso real.

## Segredos

Nunca em `HERMES.md`, `SKILL.md`, git, prompt ou saída de banco. Apenas variáveis de
ambiente; secret manager em produção.

Em HML/PRD, **arquivo antes de variável**: `REVENUE_API_KEY_FILE` tem precedência sobre
`REVENUE_API_KEY`, e é o formato que Docker secret e volume de Kubernetes montam.
Variável de ambiente vaza em `docker inspect`, em `/proc/{pid}/environ` e em qualquer
dump de processo; arquivo com `0600` não. `scripts/hermes-setup.sh` já grava assim.

Rotação: `REVENUE_API_KEY` aceita lista separada por vírgula, e todas valem ao mesmo
tempo. Trocar credencial é publicar `nova,antiga`, migrar o consumidor e depois remover
a antiga — sem janela de indisponibilidade. Ver
[ADR-0009](adr/0009-credencial-de-borda-da-revenue-api.md).

## RLS

Habilitada em todas as tabelas desde `0009`, sem policies: nega por padrão para
`anon`/`authenticated`, enquanto `service_role` faz bypass. A migração para o Supabase
não exige retrabalho de segurança.

O agente não recebe `service_role`. Ele lê pelo MCP, que fala HTTP com a Revenue API —
e desde o ADR-0009 essa chamada é autenticada: a API recusa qualquer rota que não seja
`/health` sem `Authorization: Bearer`.
