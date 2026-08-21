using System.Reflection;

namespace AutoHous.Revenue.Application.Tests;

public class RawCompanyRowHashTests
{
    private static readonly RawCompanyRow Base = new()
    {
        Cnpj = "11222333000181",
        RazaoSocial = "GRUPO VENTO SUL VEICULOS LTDA",
        CnaePrincipal = "4511101",
        SituacaoCadastral = "02",
        Municipio = "Bauru",
        Uf = "SP"
    };

    [Fact]
    public void Mesma_linha_produz_o_mesmo_hash()
    {
        Assert.Equal(RawCompanyRowHash.Of(Base), RawCompanyRowHash.Of(Base with { }));
    }

    [Fact]
    public void Caixa_e_espaco_nao_criam_linha_nova()
    {
        // "Vento Sul" e " VENTO SUL " sao a mesma linha vinda de duas ferramentas
        // diferentes; tratar como duas encheria o lote de duplicata inexplicavel.
        var variante = Base with { RazaoSocial = "  grupo vento sul veiculos ltda  " };

        Assert.Equal(RawCompanyRowHash.Of(Base), RawCompanyRowHash.Of(variante));
    }

    [Fact]
    public void Campo_ausente_e_campo_vazio_sao_equivalentes()
    {
        var comNulo = Base with { NomeFantasia = null };
        var comVazio = Base with { NomeFantasia = "   " };

        Assert.Equal(RawCompanyRowHash.Of(comNulo), RawCompanyRowHash.Of(comVazio));
    }

    [Fact]
    public void Campos_deslocados_nao_colidem()
    {
        // Sem separador, ("AB", "C") e ("A", "BC") produziriam a mesma
        // concatenacao — e duas empresas distintas viveriam sob um hash so.
        var a = Base with { RazaoSocial = "AB", NomeFantasia = "C" };
        var b = Base with { RazaoSocial = "A", NomeFantasia = "BC" };

        Assert.NotEqual(RawCompanyRowHash.Of(a), RawCompanyRowHash.Of(b));
    }

    /// <summary>
    /// A guarda que importa: um campo novo em <see cref="RawCompanyRow"/> que
    /// nao entre no hash faz a recarga do mes seguinte ser descartada como "linha
    /// repetida" pelo indice <c>companies_raw_batch_hash_uq</c> - sem erro, sem
    /// log, sem ninguem perceber que a atualizacao se perdeu.
    ///
    /// Por reflexao, e nao por lista escrita a mao, porque uma lista escrita a
    /// mao tem exatamente o mesmo defeito que ela deveria pegar.
    /// </summary>
    [Fact]
    public void Todo_campo_da_linha_afeta_o_hash()
    {
        var properties = typeof(RawCompanyRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
            .ToList();

        Assert.NotEmpty(properties);

        var baseline = RawCompanyRowHash.Of(Base);
        var ignored = new List<string>();

        foreach (var property in properties)
        {
            var mutated = Base with { };
            property.SetValue(mutated, $"MUDOU-{property.Name}");

            if (RawCompanyRowHash.Of(mutated) == baseline) ignored.Add(property.Name);
        }

        Assert.True(ignored.Count == 0,
            $"Campos fora do hash — a recarga que so mudar um deles sera descartada em silencio: " +
            $"{string.Join(", ", ignored)}");
    }
}
