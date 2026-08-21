namespace AutoHous.Revenue.Application;

/// <summary>
/// Geracao de identificadores como porta. UUID v7 e ordenavel no tempo, o que
/// mantem a localidade de insercao nos indices B-tree.
/// </summary>
public interface IIdentifierGenerator
{
    Guid NewId();
}

public sealed class GuidV7Generator : IIdentifierGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}
