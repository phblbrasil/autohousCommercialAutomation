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
| `contacts`, `contact_channels` | **PII de pessoa física** | vazias; entram com o People Finder (A05) |

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

**A política de retenção continua indefinida**, para as três tabelas de PII. É
pré-requisito do People Finder, não desta entrega — e o opt-in existe justamente
para que a base não acumule PII antes de a política existir.

## Segredos

Nunca em `HERMES.md`, `SKILL.md`, git, prompt ou saída de banco. Apenas variáveis de
ambiente; secret manager em produção.

## RLS

Habilitada em todas as tabelas desde `0009`, sem policies: nega por padrão para
`anon`/`authenticated`, enquanto `service_role` faz bypass. A migração para o Supabase
não exige retrabalho de segurança.

O agente não recebe `service_role`. Ele lê pelo MCP, que fala HTTP com a Revenue API.
