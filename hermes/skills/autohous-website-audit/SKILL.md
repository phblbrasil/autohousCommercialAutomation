---
name: autohous-website-audit
description: Audita o site de uma empresa do mercado automotivo brasileiro (concessionária, grupo, revenda) e produz um Website Audit estruturado, com evidências rastreáveis, para o Revenue Engine da AutoHous. Não atribui notas — descreve o que o site faz e deixa de fazer.
version: 1.0.0
author: AutoHous
license: MIT
metadata:
  hermes:
    tags: [Sales, Research, Web]
    requires_toolsets: [web, browser]
---

# AutoHous — Auditoria de site

## When to Use

Quando for solicitada a auditoria do site de uma conta do mercado automotivo para o
Revenue Engine da AutoHous: concessionária, grupo de concessionárias, revenda de
seminovos, locadora ou rede de oficinas.

Não use esta skill para levantar o retrato da empresa — isso é a skill
`autohous-account-research`. Não use para redigir abordagem comercial — isso é do SDR.

## O que a plataforma já mediu

Esta skill roda **depois** de uma sonda determinística. Tempo até o primeiro byte,
peso do HTML, compressão, recursos bloqueantes, HTTPS, title, meta description, h1,
canonical, dados estruturados, sitemap, robots, viewport e as assinaturas de
tecnologia no HTML **já estão registrados** e chegam na sua mensagem.

Você não os repete nem os estima. Você responde o que uma medição não alcança: o que
a página **significa** para quem vende carro.

Isso inclui, em especial, o que só existe depois do JavaScript rodar. A sonda lê o
HTML entregue; numa vitrine em SPA isso é uma casca vazia. **A contagem de veículos e
a qualidade da vitrine são suas** — navegue de verdade.

## Quick Reference

- Contrato de saída: `${HERMES_SKILL_DIR}/references/audit-schema.md`
- O que olhar numa vitrine: `${HERMES_SKILL_DIR}/references/vitrine.md`
- Perfil de cliente ideal: `${HERMES_SKILL_DIR}/../autohous-account-research/references/icp.md`
- Produtos AutoHous: `${HERMES_SKILL_DIR}/../autohous-account-research/references/products.md`

## Procedure

1. Abra a URL e **confirme a identidade** da empresa (CNPJ no rodapé, razão social).
2. Localize a **vitrine de veículos**. Navegue-a: filtros, página de detalhe, fotos,
   contagem visível.
3. Procure o estoque **fora do site** — Webmotors, iCarros, OLX, Mobiauto.
4. Mapeie os **caminhos de conversão**: formulário, WhatsApp, simulador, troca,
   agendamento.
5. **Infira integrações** de rodapé, subdomínio, vaga de emprego — sempre com evidência.
6. Liste **problemas e pontos fortes** por área, com gravidade honesta.
7. **Autoavalie** a completude.

## Regras que a plataforma verifica mecanicamente

Estas não são recomendações — o run é rejeitado se falharem:

- Todo `evidence_index` aponta para item existente em `evidence[]`.
- Toda evidência tem URL de fonte e confiança maior que zero.
- `inventory.approximate_count` maior que zero exige evidência com `claim_type`
  contendo `inventory` ou `estoque`.
- O JSON satisfaz o schema, com `additionalProperties: false` em todo objeto.

## Distinção que mais importa

`null` **não é** `false`.

`false` significa "olhei e não tem". `null` significa "não consegui verificar". Marcar
o segundo como o primeiro inventa uma dor que a empresa talvez não tenha — e essa dor
entra no Opportunity Score, prioriza a fila de execução e vira o gancho de uma
abordagem que não se sustenta.

Na dúvida: `null`, e completude menor.
