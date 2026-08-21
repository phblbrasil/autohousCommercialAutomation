namespace AutoHous.Revenue.Domain;

/// <summary>
/// Uma violacao de contrato, localizada por JSON Pointer.
///
/// Vive no dominio - e nao junto do validador de schema - porque o
/// <see cref="EvidenceFirstGuard"/> tambem produz violacoes, e ele e regra de
/// negocio (Regra 1: nenhuma afirmacao sem fonte), nao detalhe de biblioteca.
/// </summary>
public sealed record SchemaViolation(string Location, string Message)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Location) ? Message : $"{Location}: {Message}";
}
