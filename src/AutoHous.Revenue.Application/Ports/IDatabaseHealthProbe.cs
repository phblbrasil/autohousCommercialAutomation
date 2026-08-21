namespace AutoHous.Revenue.Application;

/// <summary>Checagem de alcancabilidade do armazenamento, para o endpoint /health.</summary>
public interface IDatabaseHealthProbe
{
    Task<bool> IsReachableAsync(CancellationToken ct = default);
}
