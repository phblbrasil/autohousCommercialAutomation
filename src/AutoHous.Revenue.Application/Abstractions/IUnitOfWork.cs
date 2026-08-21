namespace AutoHous.Revenue.Application;

/// <summary>
/// Limite transacional de um caso de uso.
///
/// O contrato nao expoe conexao nem transacao: quem precisa delas e o adaptador
/// de persistencia, do outro lado da porta. A versao anterior desta interface
/// devolvia <c>NpgsqlConnection</c>, o que colocava um tipo de fornecedor no
/// contrato interno (§6.1 da skill) e impedia testar qualquer caso de uso sem
/// banco real.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// Confirma o trabalho. Sem chamada explicita, o descarte faz rollback - e o
    /// que garante que uma excecao no meio da persistencia nao deixe escrita
    /// parcial.
    /// </summary>
    Task CommitAsync(CancellationToken ct = default);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken ct = default);
}
