namespace AutoHous.Revenue.Application;

/// <summary>
/// Relogio como porta (§17.3: "controle relogio, aleatoriedade e IDs por
/// portas"). O cooldown mensal e a expiracao de sinal dependem de "agora"; sem
/// esta porta, testar a virada de mes exige esperar a virada de mes.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
