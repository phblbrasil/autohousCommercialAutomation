---
name: autohous-account-research
description: Pesquisa contas do mercado automotivo brasileiro (concessionárias, grupos, revendas) e produz um Research Profile estruturado com evidências rastreáveis para o Revenue Engine da AutoHous.
version: 1.0.0
author: AutoHous
license: MIT
metadata:
  hermes:
    tags: [Sales, Research]
    requires_toolsets: [web]
---

# AutoHous — Pesquisa de conta

## When to Use

Quando for solicitado o levantamento de uma conta do mercado automotivo para o
Revenue Engine da AutoHous: concessionária, grupo de concessionárias, revenda de
seminovos, marketplace, locadora ou rede de oficinas.

Não use esta skill para redigir abordagem comercial — isso pertence à skill de SDR.

## Quick Reference

- Contrato de saída: `${HERMES_SKILL_DIR}/references/research-schema.md`
- Perfil de cliente ideal: `${HERMES_SKILL_DIR}/references/icp.md`
- Produtos AutoHous: `${HERMES_SKILL_DIR}/references/products.md`

## Procedure

1. Confirmar o domínio institucional da empresa antes de qualquer outra coleta.
   Verificar CNPJ no rodapé, razão social ou endereço. Domínio errado invalida
   toda a pesquisa.
2. Mapear unidades, marcas representadas e cobertura geográfica.
3. Estimar o estoque publicado, quando a vitrine expuser contagem ou paginação.
4. Buscar sinais dos últimos 12 meses (expansão, contratação, troca de liderança,
   relançamento de site, investimento em marketing).
5. Registrar cada afirmação em `evidence[]` com URL e `observed_at`.
6. Emitir o JSON do Research Profile, sem texto ao redor.

## Pitfalls

- **Homônimos.** Nomes de grupos automotivos se repetem entre estados. Confirmar
  por CNPJ ou cidade antes de aceitar um domínio.
- **Agregadores.** Perfis em portais de classificados não são o site institucional
  e não devem preencher `domain`.
- **Contagem de lojas inflada.** Páginas de "onde estamos" às vezes listam pontos
  de atendimento de terceiros. Preferir a página de unidades próprias.
- **Afirmar sem fonte.** Toda marca, loja e sinal precisa de `evidence_index`
  válido. A plataforma rejeita o run inteiro quando o índice não existe.
- **Inflar a completude.** `research_completeness` é usado como quality gate
  (limite de 0.70). Superestimar deixa passar pesquisa rasa.

## Verification

Antes de responder, confirmar:

- [ ] `evidence[]` tem pelo menos um item, e todo item tem URL `http(s)` e `observed_at`.
- [ ] Todo `evidence_index` em `brands`, `locations` e `signals` aponta para uma
      posição existente de `evidence[]`.
- [ ] `store_count`, se preenchido, tem evidência de `claim_type` relacionado a lojas.
- [ ] `segment`, `signal_type` e `source.type` usam apenas valores dos enums.
- [ ] A resposta é só o objeto JSON, sem prosa e sem bloco de código.
