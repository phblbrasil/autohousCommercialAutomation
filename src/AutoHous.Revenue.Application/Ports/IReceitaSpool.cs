namespace AutoHous.Revenue.Application;

/// <summary>
/// Arquivo de trabalho da carga: as linhas que sobreviveram ao filtro de origem,
/// guardadas fora do banco entre a leitura de <c>Estabelecimentos</c> e a
/// juncao com <c>Empresas</c>.
///
/// Existe por causa da ordem imposta pela propria fonte. Razao social, porte e
/// capital social vivem em <c>Empresas</c>, chaveados pela raiz do CNPJ - e so
/// depois de varrer os 5,1 GB de estabelecimentos se sabe QUAIS raizes
/// interessam. Sem o spool haveria duas saidas ruins: segurar centenas de
/// milhares de estabelecimentos em memoria durante a segunda passada, ou reler
/// os 5,1 GB.
///
/// Efeito colateral util: com o spool no disco, refazer a juncao depois de
/// corrigir um mapeamento nao exige baixar nem reler a fonte.
/// </summary>
public interface IReceitaSpool
{
    /// <summary>Descarta o conteudo anterior. Recarregar o mesmo release comeca limpo.</summary>
    Task ResetAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Acrescenta um bloco. Em bloco e nao linha a linha: sao centenas de
    /// milhares de linhas, e um await por linha domina o custo da passada.
    /// </summary>
    Task AppendAsync(string name, IReadOnlyList<RawCompanyRow> rows, CancellationToken ct = default);

    /// <summary>Le de volta, na ordem em que foi escrito.</summary>
    IAsyncEnumerable<RawCompanyRow> ReadAsync(string name, CancellationToken ct = default);

    Task DeleteAsync(string name, CancellationToken ct = default);
}
