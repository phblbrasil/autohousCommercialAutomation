using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Reune, numa leitura so, tudo que o Orchestrator precisa saber sobre uma
/// conta.
///
/// Uma leitura e nao seis chamadas a repositorios existentes por uma razao de
/// correcao, e nao de desempenho: a decisao e uma funcao do retrato INTEIRO, e
/// seis leituras independentes veem seis instantes diferentes. Uma auditoria
/// concluindo entre a terceira e a quarta faria o Orchestrator decidir sobre um
/// estado que nunca existiu.
/// </summary>
public interface IAccountProgressRepository
{
    Task<AccountProgress?> GetAsync(Guid accountId, CancellationToken ct = default);
}
