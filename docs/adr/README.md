# Architecture Decision Records

Um ADR por decisão que seria cara de reverter. O formato segue o §28 da skill de
arquitetura: contexto, opções, decisão, consequências — e um **gatilho de
revisão**, que é o campo que impede um ADR de virar dogma.

| # | Decisão | Status |
|---|---|---|
| [0001](0001-camada-de-aplicacao.md) | Camada de aplicação explícita | Aceito |
| [0002](0002-unidade-de-trabalho-sem-npgsql.md) | `IUnitOfWork` sem tipo de fornecedor | Aceito |
| [0003](0003-dapper-em-vez-de-orm.md) | Dapper e SQL explícito em vez de ORM | Aceito |
| [0004](0004-linha-crua-antes-da-normalizacao.md) | Estágio de linha crua na captura | Aceito |
| [0005](0005-scoring-deterministico.md) | Scoring determinístico, sem LLM | Aceito |
| [0006](0006-fila-de-revisao-de-merge.md) | Fila de revisão em vez de merge otimista | Aceito |
| [0007](0007-filtro-de-cnae-na-origem.md) | Filtro de CNAE na origem do seed | Aceito |
| [0008](0008-socios-e-pii.md) | Quadro societário em tabela e migration próprias | Aceito |

Decisões anteriores a este diretório estão descritas em prosa em
[architecture.md](../architecture.md) e [data-model.md](../data-model.md).
