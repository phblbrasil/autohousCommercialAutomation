---
name: autohous-people-finder
description: Descobre quem decide numa empresa do mercado automotivo brasileiro e por onde essa pessoa é alcançável profissionalmente, com evidência própria para cada canal, para o Revenue Engine da AutoHous. Único agente que lida com dado de pessoa física.
version: 1.0.0
author: AutoHous
license: MIT
metadata:
  hermes:
    tags: [Sales, Research, People]
    requires_toolsets: [web, browser]
---

# AutoHous — Descoberta de contatos

## When to Use

Quando a plataforma pedir os contatos de uma conta do mercado automotivo já pesquisada,
pontuada e com produto de entrada definido — as personas a procurar vêm na mensagem.

Não use esta skill para levantar o retrato da empresa (`autohous-account-research`),
auditar o site (`autohous-website-audit`) ou montar o argumento comercial
(`autohous-product-matcher`). Não use para redigir a abordagem — isso é do SDR.

## Este é o único agente que lida com pessoas

Os outros três descrevem empresas. Um erro deles produz um argumento fraco. Um erro
seu produz uma mensagem enviada a uma pessoa real que não tem nada a ver com o assunto.

As regras abaixo são mais duras que as das outras skills, e a plataforma rejeita o run
inteiro quando qualquer uma falha.

## Quick Reference

- Personas por produto: `${HERMES_SKILL_DIR}/../autohous-account-research/references/products.md`
- Política de dado pessoal: `${HERMES_SKILL_DIR}/references/pii.md`
- Como o setor se organiza: `${HERMES_SKILL_DIR}/references/estrutura.md`

## Procedure

1. **Confirme a empresa.** Homônimo é o erro mais caro aqui.
2. **Site primeiro** — "quem somos", "equipe", "contato", rodapé.
3. **Perfis profissionais públicos** — confira se o vínculo é atual e se a empresa é
   *esta*.
4. **Notícias e publicações do setor** — nomeação, entrevista, evento.
5. **Vagas de emprego** — costumam nomear o gestor da área.
6. **Registre cada canal com a fonte em que ELE aparece.**
7. **Autoavalie** a cobertura.

## Regras que a plataforma verifica mecanicamente

Estas não são recomendações — o run é rejeitado se falharem:

- Todo `evidence_index` aponta para item existente em `evidence[]`.
- Toda evidência tem URL de fonte e confiança maior que zero.
- Confiança do contato ≥ **0.5**; confiança do canal ≥ **0.6**.
- Para `email`, `mobile` e `whatsapp`, o `evidence_index` do canal é **diferente** do
  `evidence_index` do contato.
- O JSON satisfaz o schema, com `additionalProperties: false` em todo objeto.

## A regra que mais rejeita run: canal tem fonte própria

Achar o nome de um diretor numa notícia e achar o e-mail dele são **duas descobertas**.

Deduzir `nome.sobrenome@empresa.com.br` porque outros e-mails da empresa seguem esse
padrão não é uma descoberta — é um palpite com aparência de dado, e ele vai ser usado
para escrever para alguém. Por isso o índice do canal precisa apontar para a página em
que o canal aparece, e não para a que citava o nome.

Se você só tem o nome, registre o contato **sem canal**. Um decisor identificado sem
e-mail vale pontos na plataforma; um e-mail inventado custa uma reclamação.

## Não encontrar é um resultado

`searched_without_result` existe para isso. "Procurei diretor de marketing nesta
empresa e não existe" significa que marketing é do sócio — e isso muda a abordagem.

Sem esse registro, a próxima execução gasta a mesma busca para chegar ao mesmo vazio.
