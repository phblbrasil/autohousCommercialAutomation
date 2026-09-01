---
name: autohous-product-matcher
description: Transforma um diagnóstico já calculado — fit de produto, critério por critério — no argumento comercial correspondente, com evidências rastreáveis, para o Revenue Engine da AutoHous. Não atribui nota nem escolhe produto: a aritmética é da plataforma.
version: 1.0.0
author: AutoHous
license: MIT
metadata:
  hermes:
    tags: [Sales, Research]
    requires_toolsets: [web]
---

# AutoHous — Casamento de produto

## When to Use

Quando a plataforma pedir o argumento comercial de uma conta do mercado automotivo já
pesquisada, auditada e pontuada.

Não use esta skill para levantar o retrato da empresa — isso é
`autohous-account-research`. Não use para auditar o site — isso é
`autohous-website-audit`. Não use para redigir a mensagem de abordagem — isso é do SDR.

## O que a plataforma já decidiu

Esta skill roda **depois** de um cálculo determinístico. Quanto cada produto serve a
esta conta, e **qual é a porta de entrada**, chegam prontos na sua mensagem, critério
por critério e com os pontos de cada um.

Você não recalcula, não discorda e não reordena. O motivo é o ADR-0005: "por que o
MotorHub caiu de 78 para 51?" precisa ter resposta auditável, e uma nota gerada por
modelo não tem.

Seu trabalho é o que a aritmética não alcança — transformar

> `canais_externos: 25/25 — estoque publicado em 3 canais externos`

na frase que faz um diretor de operações dizer "é exatamente isso".

## Quick Reference

- Produtos AutoHous: `${HERMES_SKILL_DIR}/../autohous-account-research/references/products.md`
- Perfil de cliente ideal: `${HERMES_SKILL_DIR}/../autohous-account-research/references/icp.md`
- Objeções comuns do setor: `${HERMES_SKILL_DIR}/references/objecoes.md`

## Procedure

1. Leia o **diagnóstico** que veio na mensagem: os critérios, os pontos, e o que está
   marcado como **não observado**.
2. Para cada produto pedido, escolha o critério mais forte **e verificável** como
   âncora do `angle`.
3. Junte a **evidência** de cada afirmação. A nota da plataforma diz que o fato é
   verdade; o SDR vai precisar mostrar onde ele se vê.
4. Escreva os **motivos**, um por critério que sustenta o produto.
5. Antecipe as **objeções** dessa persona nesse setor.
6. **Restrinja as personas** quando a operação indicar que ali quem decide é outro.
7. Registre **desqualificadores**, se houver.

## Regras que a plataforma verifica mecanicamente

Estas não são recomendações — o run é rejeitado se falharem:

- Todo `evidence_index` aponta para item existente em `evidence[]`.
- Toda evidência tem URL de fonte e confiança maior que zero.
- Nenhum pitch vem sem ao menos um motivo com evidência.
- Toda persona em `recommended_personas` pertence à lista daquele produto no catálogo.
- O JSON satisfaz o schema, com `additionalProperties: false` em todo objeto.

## Critério não observado não é critério zerado

O diagnóstico marca cada critério como observado ou não. `vitrine: 0/40 — sem auditoria
de vitrine` **não significa** que a vitrine é boa: significa que ninguém olhou.

Construir o ângulo sobre um critério não observado produz a pior falha possível desta
etapa — uma afirmação confiante sobre algo que a plataforma explicitamente marcou como
desconhecido. Use os critérios observados, e deixe a confiança do pitch refletir
quantos deles você tinha.
