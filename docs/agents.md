# Camada de agentes

## Dois fatos da documentação oficial do Hermes que moldam tudo

### 1. Não existe endereçamento de agente por nome

`POST /v1/runs` não recebe "qual agente executar". `delegate_task` é dinâmico,
decidido pelo modelo em runtime, e a documentação não descreve como definir tipos de
agente customizados.

**Consequência:** os seis agentes do §17 do blueprint são conceitos **da aplicação**.
O worker escolhe prompt + skill + schema por tipo de run. `agent_runs.agent_name` é
rótulo nosso, útil para observabilidade e custo, não uma entidade do Hermes.

### 2. Skills não têm structured output forçado

Não há `response_format` garantido. O modelo devolve texto.

**Consequência:** `StructuredOutputValidator` não é hardening — é caminho crítico. Sem
ele, o sistema inteiro repousa sobre a esperança de que o LLM formate JSON corretamente.

O caso de uso depende da porta `IStructuredOutputValidator`, não da classe: ele
precisa da capacidade "transformar texto em `ResearchProfile` válido", não da
implementação "JSON Schema draft 2020-12 via JsonSchema.Net".

## Pipeline de validação

```
texto do agente
  ↓ JsonPayloadExtractor      tolera cerca ```json, prosa, vírgula sobrando
  ↓ JSON Schema               additionalProperties: false, enums, format
  ↓ EvidenceFirstGuard        índices de evidência apontam para itens reais
  ↓ desserialização
ResearchProfile
```

Falha em qualquer etapa → **uma** tentativa de reparo devolvendo as violações ao
agente → nova falha encerra o run com `research_runs.status='failed'` e o motivo em
`error`. Nunca escrita parcial.

Uma tentativa e não várias: se o modelo não acerta com os erros em mãos, o problema é
do prompt, e insistir só queima orçamento.

## EvidenceFirstGuard

Vive no **domínio**, e não nesta camada: é a Regra 1 da governança, regra de
negócio, não detalhe de biblioteca. O schema sabe validar "`evidence_index` é um
inteiro ≥ 0"; ele não sabe quantas evidências existem. O guard fecha essa lacuna:

- todo `evidence_index` em `brands`, `locations` e `signals` aponta para item existente;
- toda evidência tem URL de fonte e confiança > 0;
- `store_count` declarado exige evidência de `claim_type` relacionado a lojas.

Sem isso, a Regra 1 do §25 seria só uma frase no documento — e uma abordagem do tipo
"vi que vocês têm 12 lojas" sairia sem lastro.

## Onde cada peça vive

| Peça | Projeto | Por quê |
|---|---|---|
| `IAgentRuntime` | Application | quem precisa da capacidade declara o contrato |
| `HermesAgentRuntime`, `FixtureAgentRuntime` | Agents | implementações |
| `IStructuredOutputValidator` | Application | o caso de uso depende da capacidade |
| `StructuredOutputValidator` | Agents | JSON Schema é detalhe de biblioteca |
| `EvidenceFirstGuard` | Domain | Regra 1 é política de negócio |
| `IResearchPromptBuilder` | Application | o caso de uso precisa do texto |
| `ResearchPromptBuilder` | Agents | ler prompt versionado de disco é I/O |
| `ExecuteResearchRunUseCase` | Application | nada nele é específico de rodar sob um `BackgroundService` |

O último item é o que permite testar o ciclo de reparo inteiro com portas falsas,
sem Postgres e sem Hermes.

## Runtimes

| Runtime | Quando | Custo | Determinístico |
|---|---|---|---|
| `fixture` | desenvolvimento, CI | zero | sim |
| `hermes` | ativação, produção | real | não |

O fixture lê respostas gravadas de `tests/fixtures/agent-runs/{agente}/{cenário}.json`.
O cenário vem do payload do evento, o que permite exercitar sucesso, ciclo de reparo e
falha dura pelo mesmo caminho de código de produção.

`AGENT_RUNTIME` inválido falha na **inicialização**. Cair silenciosamente no fixture em
produção geraria pesquisas falsas com aparência de sucesso.

## Aviso sobre o envelope de `/v1/runs`

A documentação pública lista os endpoints de `/v1/runs` mas não fixa o formato exato
das respostas. `HermesAgentRuntime` extrai o texto final de forma tolerante, testando
as formas conhecidas. Isso deve ser confirmado contra o servidor real na ativação
(ARI-58); divergindo, `HermesOptions.Transport = Chat` usa o envelope OpenAI de
`/v1/chat/completions`, que é especificado com exatidão.
