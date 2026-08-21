namespace AutoHous.Revenue.Domain;

/// <summary>Uma celula do agregado nacional: quantos estabelecimentos existem neste recorte.</summary>
public sealed record CnaeStatRow(
    string Uf,
    string Cnae,
    string SituacaoCadastral,
    string MatrizFilial,
    long Establishments);

/// <summary>Mesma contagem, com granularidade de municipio. So para o universo do catalogo.</summary>
public sealed record MunicipioStatRow(
    string Uf,
    string MunicipioCodigo,
    string Cnae,
    string SituacaoCadastral,
    long Establishments);

/// <summary>
/// Agregado de mercado da base da Receita Federal.
///
/// Contagem e regra de negocio aqui, nao detalhe de leitura de arquivo: quais
/// dimensoes cruzam, o que conta como "nao informado" e onde a granularidade de
/// municipio para de valer sao decisoes de produto. Por isso mora no dominio, e
/// e testavel sem arquivo, sem zip e sem banco.
///
/// A instancia e alimentada UMA vez por estabelecimento lido, ANTES de qualquer
/// filtro. E o que impede o filtro de CNAE da captura de esconder o que
/// descartou: uma revenda inativa nao entra em <c>companies_raw</c>, mas continua
/// contada aqui.
/// </summary>
public sealed class MarketStatisticsAccumulator
{
    private readonly Dictionary<CnaeKey, long> _byCnae = [];
    private readonly Dictionary<MunicipioKey, long> _byMunicipio = [];

    /// <summary>Quantas linhas passaram pelo acumulador, cruzamento nenhum.</summary>
    public long Scanned { get; private set; }

    /// <summary>
    /// Registra um estabelecimento.
    ///
    /// <paramref name="municipioCodigo"/> so e cruzado quando o CNAE pertence ao
    /// <see cref="CnaeCatalog"/>: a grade completa seria 5.572 municipios x ~1.350
    /// CNAEs x situacoes, e ninguem consulta a concentracao municipal de cultivo
    /// de arroz para vender software automotivo.
    /// </summary>
    public void Observe(
        string? uf,
        string? cnae,
        string? situacaoCadastral,
        string? matrizFilial,
        string? municipioCodigo)
    {
        Scanned++;

        // "Nao informado" e a string vazia, e nao NULL nem um sentinela
        // inventado: as colunas fazem parte da chave primaria da tabela de
        // destino, e chave nao aceita NULL. A RF deixa UF em branco para
        // estabelecimento no exterior - descartar essas linhas silenciosamente
        // seria o oposto do que este agregado existe para fazer.
        var ufKey = Blank(uf).ToUpperInvariant();
        var cnaeKey = CnaeCatalog.NormalizeCode(cnae) ?? Blank(cnae);
        var situacaoKey = Blank(situacaoCadastral);
        var matrizKey = Blank(matrizFilial);

        Increment(_byCnae, new CnaeKey(ufKey, cnaeKey, situacaoKey, matrizKey));

        if (cnaeKey.Length == 0 || !CnaeCatalog.IsInUniverse(cnaeKey)) return;

        var municipioKey = Blank(municipioCodigo);
        if (municipioKey.Length == 0) return;

        Increment(_byMunicipio, new MunicipioKey(ufKey, municipioKey, cnaeKey, situacaoKey));
    }

    /// <summary>
    /// Ordenacao estavel e deliberada: dois releases da mesma base produzem a
    /// mesma sequencia de linhas, entao um diff entre cargas mostra mudanca de
    /// mercado e nao mudanca de ordem de dicionario.
    /// </summary>
    public IReadOnlyList<CnaeStatRow> ByCnae =>
    [
        .. _byCnae
            .OrderBy(e => e.Key.Uf, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Cnae, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Situacao, StringComparer.Ordinal)
            .ThenBy(e => e.Key.MatrizFilial, StringComparer.Ordinal)
            .Select(e => new CnaeStatRow(
                e.Key.Uf, e.Key.Cnae, e.Key.Situacao, e.Key.MatrizFilial, e.Value))
    ];

    public IReadOnlyList<MunicipioStatRow> ByMunicipio =>
    [
        .. _byMunicipio
            .OrderBy(e => e.Key.MunicipioCodigo, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Cnae, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Situacao, StringComparer.Ordinal)
            .Select(e => new MunicipioStatRow(
                e.Key.Uf, e.Key.MunicipioCodigo, e.Key.Cnae, e.Key.Situacao, e.Value))
    ];

    private static void Increment<TKey>(Dictionary<TKey, long> target, TKey key) where TKey : notnull =>
        target[key] = target.TryGetValue(key, out var current) ? current + 1 : 1;

    private static string Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private readonly record struct CnaeKey(string Uf, string Cnae, string Situacao, string MatrizFilial);
    private readonly record struct MunicipioKey(string Uf, string MunicipioCodigo, string Cnae, string Situacao);
}
