using System.Security.Cryptography;
using System.Text;

namespace AutoHous.Revenue.Application;

/// <summary>
/// Identidade de uma linha crua dentro de um lote.
///
/// Deriva dos campos, e nao do JSON serializado, para que reordenar colunas no
/// arquivo de origem nao invente linhas novas.
///
/// Cobre TODOS os campos de <see cref="RawCompanyRow"/>, e isso e requisito e
/// nao zelo: o indice <c>companies_raw_batch_hash_uq</c> transforma hash igual em
/// no-op silencioso. Se o hash ignorasse, digamos, telefone, a recarga do mes
/// seguinte em que so o telefone mudou seria descartada como "linha repetida" e a
/// atualizacao se perderia sem erro nenhum - o oposto do que
/// <c>docs/ingestion.md</c> promete em "Recarga e atualizacao, nao no-op".
///
/// <c>RawCompanyRowHashTests</c> percorre as propriedades por reflexao e falha se
/// alguma nao afetar o resultado: um campo novo esquecido aqui quebra o build.
/// </summary>
public static class RawCompanyRowHash
{
    /// <summary>
    /// Separador de unidade. Nao ocorre em razao social nem em municipio, entao
    /// duas linhas diferentes nao podem colidir por concatenacao.
    /// </summary>
    private const char Separator = '\u001f';

    public static string Of(RawCompanyRow row)
    {
        var canonical = string.Join(Separator,
            Key(row.Cnpj),
            Key(row.RazaoSocial),
            Key(row.NomeFantasia),
            Key(row.CnaePrincipal),
            Key(row.SituacaoCadastral),
            Key(row.Municipio),
            Key(row.Uf),
            Key(row.MatrizFilial),
            Key(row.NaturezaJuridica),
            Key(row.Porte),
            Key(row.CapitalSocial),
            Key(row.DataInicioAtividade),
            Key(row.DataSituacaoCadastral),
            Key(row.MotivoSituacaoCadastral),
            Key(row.CnaesSecundarios),
            Key(row.MunicipioCodigo),
            Key(row.Cep),
            Key(row.Logradouro),
            Key(row.Numero),
            Key(row.Complemento),
            Key(row.Bairro),
            Key(row.Telefone1),
            Key(row.Telefone2),
            Key(row.Email),
            Key(row.OpcaoSimples),
            Key(row.OpcaoMei));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Caixa alta e espacos aparados: "Vento Sul" e " VENTO SUL " sao a mesma
    /// linha vinda de duas ferramentas diferentes, e tratar como duas encheria o
    /// lote de duplicata que ninguem consegue explicar.
    /// </summary>
    private static string Key(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
