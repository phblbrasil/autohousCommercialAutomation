# ADR-0002 — `IUnitOfWork` sem tipo de fornecedor

**Data:** 2026-08-20 · **Status:** Aceito

## Contexto

A interface original expunha a conexão:

```csharp
public interface IUnitOfWork : IAsyncDisposable
{
    NpgsqlConnection Connection { get; }
    NpgsqlTransaction Transaction { get; }
    Task CommitAsync(CancellationToken ct = default);
}
```

Enquanto o contrato expuser `NpgsqlConnection`, qualquer consumidor precisa
referenciar o driver — e nenhum caso de uso é testável sem Postgres real. O §6.1
da skill de arquitetura é explícito: objetos de SDK não circulam por contratos
internos.

## Opções consideradas

1. **Manter a conexão no contrato.** Zero trabalho, mantém o acoplamento.
2. **Mover toda composição transacional para dentro da Infrastructure**, em
   scripts transacionais por operação. Resolve o vazamento, mas move a definição
   do limite transacional — que é decisão de negócio — para o adaptador.
3. **Contrato mínimo na Application; a infraestrutura desembrulha.**

## Decisão

Opção 3:

```csharp
public interface IUnitOfWork : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}
```

`NpgsqlUnitOfWork` implementa a porta com `Connection` e `Transaction` marcados
`internal`. Os repositórios recuperam a transação concreta por extensões
internas, `uow.Db()` e `uow.Tx()`.

## Consequências

**Positivas**

- O caso de uso declara *"isto é uma transação"* sem saber o que é uma transação
  no armazenamento.
- `FakeUnitOfWork` cabe em quinze linhas, e o teste verifica que o commit
  aconteceu.
- Migrar para outro armazenamento não toca em nenhum caso de uso.

**Negativas**

- Um cast em tempo de execução dentro da infraestrutura.

**Por que o cast é aceitável**

A única implementação de `IUnitOfWork` registrada no contêiner é
`NpgsqlUnitOfWork`, e o cast está confinado a um único método privado que lança
mensagem clara. A alternativa — expor a conexão na porta — devolveria um tipo de
fornecedor ao núcleo, que é justamente o problema. `DependencyRuleTests` verifica
que nenhum membro da porta devolve tipo do namespace `Npgsql`.

## Gatilho de revisão

Se aparecer uma segunda implementação de `IUnitOfWork` (por exemplo, um
armazenamento em memória para testes de aceitação), o cast vira risco real e o
desenho precisa de despacho polimórfico.
