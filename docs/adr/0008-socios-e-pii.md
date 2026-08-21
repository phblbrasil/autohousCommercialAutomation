# ADR-0008 — Quadro societário em tabela e migration próprias

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

Os Dados Abertos CNPJ publicam o arquivo `Socios`: nome do sócio, CPF
parcialmente mascarado na origem (art. 129 §2º da Lei 13.473/2017),
qualificação, data de entrada, faixa etária e representante legal.

O dado é valioso: é a semente do People Finder (A05) e o caminho mais direto para
descobrir grupos econômicos que não compartilham raiz de CNPJ nem nome parecido —
exatamente o caso que o `AccountGroupResolver` não resolve sozinho.

E ele muda a natureza da base. Até aqui `docs/governance.md` podia afirmar, com
precisão, que a plataforma guardava **dado cadastral de pessoa jurídica, público
por natureza — não PII de pessoa física**. Carregar sócios torna essa frase
falsa, e o frame 09 da V2 lista *"PII minimization: armazenar só o necessário"*
como guardrail P0.

## Opções consideradas

1. **Não carregar.** Preserva a classificação da base e abre mão do insumo.
2. **Carregar junto com o resto**, como mais uma tabela da migration `0013`.
   Simples, e mistura numa migração só o que tem risco de privacidade e o que não
   tem. Reverter passa a exigir uma migration nova para cada coluna.
3. **Carregar em tabela e migration próprias, atrás de opt-in explícito.**

## Decisão

**Opção 3**, com três separações deliberadas:

- **Migration própria** (`0014_company_partners.sql`). Parar de guardar o dado é
  um `drop table`, e não um projeto: nada em account graph, evidência ou score
  depende dela.
- **Opt-in por execução.** A carga só toca o arquivo com `--socios`; sem a flag,
  `Socios*.zip` nem é baixado. Carregar PII é um ato, não um efeito colateral de
  rodar a carga mensal.
- **Porta própria.** `ICompanyPartnerRepository` existe separada das demais para
  que "quem escreve PII de pessoa física" seja legível no grafo de dependências,
  e não uma linha no meio de um repositório grande.

Além disso: o CPF é gravado **exatamente como a Receita entrega**, mascarado.
Não há caminho de código que o desmascare, e o nome da coluna
(`cpf_cnpj_mascarado`) diz isso a quem for consultar.

O recorte também é mínimo: só entram sócios das raízes de CNPJ que sobreviveram
ao filtro do [ADR-0007](0007-filtro-de-cnae-na-origem.md). O quadro societário de
uma padaria não entra na base por estar no mesmo arquivo.

## Consequências

- `docs/governance.md` passa a declarar `company_partners` como PII, ao lado de
  `contacts` e `contact_channels`.
- A política de retenção continua **indefinida** — para as três tabelas. Ela é
  pré-requisito do People Finder (A05), não desta entrega, e o opt-in existe para
  que a base não acumule PII antes de a política existir.
- RLS habilitada como em toda tabela desde a `0009`: nega por padrão para
  `anon`/`authenticated`.
- O agente não alcança a tabela: ele lê pelo MCP, que fala HTTP com a Revenue API,
  e não há endpoint expondo sócios.

## Gatilho de revisão

Rever quando:

- a política de retenção de PII do frame 09 for definida — ela pode exigir prazo,
  anonimização ou consentimento que esta tabela não implementa;
- o People Finder (A05) entrar e passar a ser o consumidor real do dado;
- a carga de sócios deixar de ser opt-in por qualquer motivo — nesse momento a
  decisão terá sido revertida na prática, e o ADR precisa dizer isso.
