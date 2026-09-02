# AutoHous Revenue Engine — instruções do repositório

## Antes de qualquer coisa: leia o HANDOFF.md

**[HANDOFF.md](HANDOFF.md) é o primeiro arquivo a ler em toda sessão.** Ele diz o
que está pronto, o que está pela metade, o que já foi decidido e não deve ser
refeito, e quais são os próximos passos em ordem.

Sem ele, o caminho natural é reabrir discussões encerradas e refazer trabalho que
já existe.

**Ao terminar uma sessão, atualize-o.** Um handoff desatualizado é pior que
nenhum: ele faz a próxima pessoa confiar em algo que já mudou.

---

## Ambiente: rode tudo no WSL

Não é preferência. O Smart App Control do Windows **bloqueia DLL recém-compilado**
deste projeto — depois de qualquer build, o binário novo não tem reputação e o
processo não sobe (`An Application Control policy has blocked this file`).

```bash
wsl -d Ubuntu-24.04
cd /mnt/d/projects/autohousCommercialAutomation
```

O Postgres fica no Docker Desktop e responde em `localhost:5433` de dentro do WSL.

---

## Testes

```bash
./scripts/test.sh
```

**Nunca passe `--nologo` para `dotnet test`.** No modo Microsoft.Testing.Platform,
eleito no `global.json`, a flag faz o host localizar os módulos e não iniciar
nenhum — o resultado é "Zero tests ran" com código 5, que parece suíte vazia ou
ambiente quebrado e não é nem um nem outro. O script fixa a invocação correta.

Se o build falhar com `MSB3021: Access to the path ... is denied`, há um processo
do **Windows** rodando dos mesmos `bin/` e segurando os DLLs. Pare a API e o
Worker do lado Windows.

---

## Armadilhas deste schema

**Coluna `character(n)` comparada com parâmetro string faz seq scan.** O Dapper
manda `string`, o Npgsql tipa como `text`, e o Postgres reescreve para
`(coluna)::text = $1::text` — o cast do lado da coluna, que nenhum índice serve.
Medido em `companies_cnpj`: **50,7 ms contra 0,076 ms**.

Sempre use cast explícito:

```sql
where cnpj = cast(@Cnpj as char(14))
```

Colunas afetadas: `companies_cnpj.cnpj/uf`, `company_partners.cnpj_basico`,
`account_merge_candidates.incoming_cnpj/incoming_uf`, `accounts.state`,
`account_locations.state`.

**Chave estrangeira precisa de índice do lado que referencia.** O Postgres indexa
o lado referenciado, nunca o outro — e sem ele todo `DELETE` na tabela apontada
varre a tabela que aponta. Ver migration `0016`.

**O operador `%` do `pg_trgm` não usa o limiar da sua cláusula.** Ele usa a GUC
`pg_trgm.similarity_threshold` (padrão 0.30). Alinhe com `set_limit()` na mesma
conexão, senão o índice arrasta candidatos que a consulta vai descartar.

---

## Regra central do produto

```
LLM sugere; plataforma valida.
```

O agente produz fatos com evidência rastreável. **A aritmética é sempre da
plataforma** — Opportunity Score, notas de auditoria, fit de produto. Um modelo
que devolve nota torna impossível responder "por que esta conta caiu de 82 para
68?", e essa resposta é o produto.

Toda afirmação do agente precisa de `evidence_index` apontando para evidência
real. O `EvidenceFirstGuard` impõe isso mecanicamente; não contorne.

---

## Documentos, em ordem de leitura

| | |
|---|---|
| [HANDOFF.md](HANDOFF.md) | onde paramos — **sempre primeiro** |
| [docs/mapa.md](docs/mapa.md) | onde fica cada coisa — o índice do repositório |
| [HERMES.md](HERMES.md) | o que o agente pode e não pode fazer |
| [docs/hermes-runbook.md](docs/hermes-runbook.md) | passo a passo para rodar o Hermes |
| [docs/agents.md](docs/agents.md) | camada de agentes, transporte, validação |
| [docs/carga-receita-otimizacao.md](docs/carga-receita-otimizacao.md) | diagnóstico de desempenho da carga |
| [docs/deploy-railway.md](docs/deploy-railway.md) | deploy em container |
| [docs/adr/](docs/adr/) | decisões com contexto e gatilho de revisão |
