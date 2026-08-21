# ADR-0007 — Filtro de CNAE na origem do seed

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

A camada 01 passou a ler a fonte primária: os Dados Abertos CNPJ da Receita
Federal. O release `2026-08` tem **7,3 GB comprimidos** e cerca de **63 milhões
de estabelecimentos**. Descomprimido, o arquivo de estabelecimentos sozinho passa
de 10 GB.

Desses 63 milhões, o universo automotivo do `CnaeCatalog` são ~1,5 milhão de
estabelecimentos — algo entre 2% e 3%.

Isso colide de frente com o [ADR-0004](0004-linha-crua-antes-da-normalizacao.md),
que exige gravar a linha em `companies_raw` **antes** de qualquer interpretação,
para que corrigir uma regra seja reprocessar em vez de reimportar.

## Opções consideradas

1. **Gravar os 63 milhões em `companies_raw` e filtrar depois.** Fiel à letra do
   ADR-0004. Custa dezenas de GB de `jsonb`, polui todo índice trigrama da base —
   e o `AccountGroupResolver` compara nome contra a tabela inteira — e transforma
   cada recarga mensal numa operação de infraestrutura.
2. **Filtrar tudo na origem, incluindo CNPJ inválido e nome ausente.** Barato, e
   destrói a auditabilidade: a rejeição deixa de ter motivo gravado, e um lote
   inteiro perdido por coluna mal mapeada pareceria funcionamento normal.
3. **Filtrar na origem apenas por pertencimento a conjunto**, e manter no
   `CompanyNormalizer` toda rejeição que exija julgamento.

## Decisão

**Opção 3.** O filtro na origem testa três coisas, nenhuma delas interpretativa:

| Teste | Por quê é seguro |
|---|---|
| CNAE ∈ `CnaeCatalog` | pertencimento a conjunto, sem julgamento |
| situação cadastral ativa (`--incluir-inativos` desliga) | mesmo predicado `CompanyNormalizer.IsActiveRegistration` |
| UF pedida (`--uf`, vazio = país) | recorte explícito do operador |

Tudo que exige julgamento — dígito verificador do CNPJ, nome ausente, UF que não
existe, CNAE ilegível — continua acontecendo no `CompanyNormalizer`, **depois**
que a linha já está em `companies_raw`, com `rejection_reason` gravado.

Três condições sustentam a decisão:

1. **O zip da Receita é a fonte durável.** Ele fica no cache com release e
   SHA-256 gravados em `receita_releases`. Reimportar o mesmo release é
   determinístico — ao contrário do extrato ad-hoc que o ADR-0004 tinha em mente,
   que não voltava.
2. **O que o filtro descarta não some.** `rf_cnae_stats` conta **cada uma** das
   63 milhões de linhas, por UF, CNAE, situação e matriz/filial, antes de
   qualquer filtro. A revenda baixada que não entrou em `companies_raw` continua
   contada lá.
3. **O predicado de "ativa" é um só.** `CompanyNormalizer.IsActiveRegistration` é
   público exatamente para isso: duas listas de situações ativas divergiriam no
   primeiro código novo que a Receita publicasse, e a divergência apareceria como
   linha capturada e logo rejeitada, sem ninguém entender por quê.

## Consequências

- `companies_raw` guarda ~700 mil linhas por carga nacional, não 63 milhões.
- O índice trigrama de `accounts` compara nome contra o universo automotivo, e
  não contra o cadastro empresarial do país.
- Ampliar o `CnaeCatalog` **exige recarregar o release**: as linhas fora do
  catálogo nunca chegaram ao banco. O agregado diz de antemão quantas linhas cada
  código novo traria.
- Uma coluna de CNAE mal mapeada derruba a seleção para perto de zero. O resumo
  da CLI mostra "estabelecimentos lidos" contra "universo automotivo" lado a
  lado, e a razão entre os dois é o alarme.

## Gatilho de revisão

Rever se qualquer uma destas deixar de valer:

- a AutoHous passar a prospectar fora do universo automotivo (o filtro deixa de
  ser um recorte e vira uma perda);
- o custo de armazenamento deixar de importar diante do custo de recarregar;
- a Receita passar a publicar recortes por CNAE na origem, tornando o filtro
  local redundante.
