namespace AutoHous.Revenue.Domain.Tests;

public class CnaeCatalogTests
{
    [Theory]
    [InlineData("4511-1/01")]
    [InlineData("45.11-1-01")]
    [InlineData("4511101")]
    [InlineData(" 4511 1 01 ")]
    public void Reconhece_o_mesmo_codigo_em_formatos_diferentes(string raw)
    {
        // As bases publicas circulam o mesmo CNAE em pelo menos tres grafias.
        // Comparar string crua faria a mesma empresa cair em ramos diferentes
        // conforme o arquivo de origem.
        Assert.Equal("4511101", CnaeCatalog.NormalizeCode(raw));
        Assert.Equal(AutomotiveOperation.Concessionaria, CnaeCatalog.Classify(raw)!.Operation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SP")]
    [InlineData("45111")]
    [InlineData("451110123")]
    public void Codigo_ilegivel_nao_normaliza(string? raw)
    {
        Assert.Null(CnaeCatalog.NormalizeCode(raw));
        Assert.Null(CnaeCatalog.Classify(raw));
    }

    [Fact]
    public void Codigo_valido_fora_do_universo_nao_classifica()
    {
        // 1091-1/02 e padaria: codigo perfeitamente valido, so nao e nosso.
        Assert.NotNull(CnaeCatalog.NormalizeCode("1091-1/02"));
        Assert.Null(CnaeCatalog.Classify("1091-1/02"));
    }

    [Fact]
    public void Concessionaria_e_revenda_estao_no_icp_central()
    {
        Assert.True(CnaeCatalog.Classify("4511101")!.InCoreIcp);
        Assert.True(CnaeCatalog.Classify("4511102")!.InCoreIcp);
    }

    [Fact]
    public void Oficina_e_autopecas_ficam_fora_do_icp_central()
    {
        Assert.False(CnaeCatalog.Classify("4520001")!.InCoreIcp);
        Assert.False(CnaeCatalog.Classify("4530703")!.InCoreIcp);
    }
}

public class CompanyNormalizerTests
{
    private static RawCompanyFields Valid(
        string cnpj = "11222333000181",
        string? razao = "GRUPO VENTO SUL VEICULOS LTDA",
        string? cnae = "4511-1/01",
        string? situacao = "02",
        string? uf = "SP") => new()
        {
            Cnpj = cnpj,
            RazaoSocial = razao,
            CnaePrincipal = cnae,
            SituacaoCadastral = situacao,
            Municipio = "BAURU",
            Uf = uf
        };

    [Fact]
    public void Empresa_valida_e_aceita_com_identidade_completa()
    {
        var result = CompanyNormalizer.Normalize(Valid());

        Assert.True(result.Accepted);

        var company = result.Company!;
        Assert.Equal("11222333000181", company.Cnpj);
        Assert.Equal("11222333", company.CnpjRoot);
        Assert.True(company.IsHeadquarters);
        Assert.Equal("GRUPO VENTO SUL VEICULOS", company.NormalizedName);
        Assert.Equal(AutomotiveOperation.Concessionaria, company.Cnae.Operation);
        Assert.Equal("SP", company.Uf);
    }

    [Fact]
    public void Ordem_de_filial_diferente_de_0001_nao_e_matriz()
    {
        var result = CompanyNormalizer.Normalize(Valid(cnpj: "11222333000262"));

        Assert.True(result.Accepted);
        Assert.False(result.Company!.IsHeadquarters);
        Assert.Equal("11222333", result.Company.CnpjRoot);
    }

    [Fact]
    public void Nome_fantasia_prevalece_sobre_razao_social()
    {
        // E o nome que o mercado usa - e o que o agente vai procurar na web.
        var result = CompanyNormalizer.Normalize(Valid() with { NomeFantasia = "VENTO SUL SEMINOVOS" });

        Assert.Equal("Vento Sul Seminovos", result.Company!.DisplayName);
        Assert.Equal("GRUPO VENTO SUL VEICULOS LTDA", result.Company.RazaoSocial);
    }

    [Fact]
    public void Caixa_alta_da_receita_vira_capitalizacao_legivel()
    {
        var result = CompanyNormalizer.Normalize(
            Valid(razao: "COMERCIO DE VEICULOS DA SERRA LTDA"));

        // Preposicao fica minuscula: e um nome que vai aparecer em e-mail comercial.
        Assert.Equal("Comercio de Veiculos da Serra", result.Company!.DisplayName);
    }

    [Theory]
    [InlineData("00000000000000", CompanyRejectionReason.InvalidCnpj)]
    [InlineData("11222333000199", CompanyRejectionReason.InvalidCnpj)]
    [InlineData("112223330001", CompanyRejectionReason.InvalidCnpj)]
    public void Cnpj_invalido_e_rejeitado(string cnpj, CompanyRejectionReason expected)
    {
        var result = CompanyNormalizer.Normalize(Valid(cnpj: cnpj));

        Assert.False(result.Accepted);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Empresa_sem_nenhum_nome_e_rejeitada()
    {
        var result = CompanyNormalizer.Normalize(Valid(razao: null));

        Assert.Equal(CompanyRejectionReason.MissingName, result.Reason);
    }

    [Fact]
    public void Cnae_ilegivel_e_cnae_fora_do_universo_tem_motivos_distintos()
    {
        Assert.Equal(CompanyRejectionReason.UnknownCnae,
            CompanyNormalizer.Normalize(Valid(cnae: "coluna-errada")).Reason);

        Assert.Equal(CompanyRejectionReason.OutsideUniverse,
            CompanyNormalizer.Normalize(Valid(cnae: "1091-1/02")).Reason);
    }

    [Theory]
    [InlineData("04")]
    [InlineData("BAIXADA")]
    [InlineData("SUSPENSA")]
    public void Situacao_cadastral_inativa_e_rejeitada(string situacao)
    {
        Assert.Equal(CompanyRejectionReason.InactiveRegistration,
            CompanyNormalizer.Normalize(Valid(situacao: situacao)).Reason);
    }

    [Theory]
    [InlineData("02")]
    [InlineData("2")]
    [InlineData("ativa")]
    [InlineData("ATIVA")]
    [InlineData(null)]
    public void Situacao_ativa_ou_ausente_passa(string? situacao)
    {
        Assert.True(CompanyNormalizer.Normalize(Valid(situacao: situacao)).Accepted);
    }

    [Fact]
    public void Uf_inexistente_e_rejeitada()
    {
        Assert.Equal(CompanyRejectionReason.InvalidUf,
            CompanyNormalizer.Normalize(Valid(uf: "XX")).Reason);
    }

    /// <summary>
    /// Com <c>requireCoreIcp</c>, oficina e autopecas ficam de fora. Sem ele,
    /// entram na base como universo adjacente.
    /// </summary>
    [Fact]
    public void Filtro_de_icp_central_e_opcional()
    {
        var oficina = Valid(cnae: "4520-0/01");

        Assert.True(CompanyNormalizer.Normalize(oficina).Accepted);
        Assert.Equal(CompanyRejectionReason.OutsideUniverse,
            CompanyNormalizer.Normalize(oficina, requireCoreIcp: true).Reason);
    }

    [Fact]
    public void Normalizacao_e_deterministica()
    {
        // Reprocessar companies_raw depois de corrigir uma regra so faz sentido
        // se a mesma entrada sempre produzir a mesma saida.
        var a = CompanyNormalizer.Normalize(Valid());
        var b = CompanyNormalizer.Normalize(Valid());

        Assert.Equal(a.Company, b.Company);
    }
}

/// <summary>
/// Os campos que so a base oficial da Receita traz. Eles chegam como texto cru,
/// no formato da Receita, e e aqui que viram tipo.
/// </summary>
public class ReceitaFieldNormalizationTests
{
    private static NormalizedCompany Normalize(Action<RawFieldsBuilder> configure)
    {
        var builder = new RawFieldsBuilder();
        configure(builder);

        var result = CompanyNormalizer.Normalize(builder.Build());

        Assert.True(result.Accepted, $"esperava aceitar, rejeitou por '{result.ReasonLabel}'");
        return result.Company!;
    }

    internal sealed class RawFieldsBuilder
    {
        public RawCompanyFields Fields { get; set; } = new()
        {
            Cnpj = "11222333000181",
            RazaoSocial = "GRUPO VENTO SUL VEICULOS LTDA",
            CnaePrincipal = "4511-1/01",
            SituacaoCadastral = "02",
            Uf = "SP"
        };

        public RawCompanyFields Build() => Fields;
    }

    [Theory]
    [InlineData("20150312", 2015, 3, 12)]
    [InlineData("19980101", 1998, 1, 1)]
    public void Data_no_formato_da_receita_vira_DateOnly(string raw, int y, int m, int d)
    {
        var company = Normalize(b => b.Fields = b.Fields with { DataInicioAtividade = raw });

        Assert.Equal(new DateOnly(y, m, d), company.DataAbertura);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("00000000")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("20159999")]
    public void Data_ausente_ou_impossivel_vira_nulo(string? raw)
    {
        // Gravar 01/01/0001 como data de abertura poluiria todo calculo de idade
        // da empresa com uma data que parece real.
        var company = Normalize(b => b.Fields = b.Fields with { DataInicioAtividade = raw });

        Assert.Null(company.DataAbertura);
    }

    [Fact]
    public void Capital_social_e_lido_com_virgula_decimal()
    {
        // Interpretar "1000,00" com a cultura da maquina daria cem mil num
        // servidor com locale ingles: erro de tres ordens de grandeza que passa
        // despercebido.
        var company = Normalize(b => b.Fields = b.Fields with { CapitalSocial = "1000,00" });

        Assert.Equal(1000.00m, company.CapitalSocial);
    }

    [Fact]
    public void Capital_social_com_separador_de_milhar_nao_perde_ordem_de_grandeza()
    {
        var company = Normalize(b => b.Fields = b.Fields with { CapitalSocial = "1.250.000,50" });

        Assert.Equal(1_250_000.50m, company.CapitalSocial);
    }

    [Fact]
    public void Cnaes_secundarios_sao_normalizados_e_deduplicados()
    {
        var company = Normalize(b => b.Fields = b.Fields with
        {
            CnaesSecundarios = "4511-1/02,4511102,4520001,,4530703"
        });

        Assert.Equal(["4511102", "4520001", "4530703"], company.CnaesSecundarios);
    }

    [Theory]
    [InlineData("(14) 3234-5678", "1432345678")]
    [InlineData("14 32345678", "1432345678")]
    [InlineData("00000000", null)]
    [InlineData("", null)]
    public void Telefone_guarda_so_digitos(string raw, string? expected)
    {
        var company = Normalize(b => b.Fields = b.Fields with { Telefone1 = raw });

        Assert.Equal(expected, company.Telefone1);
    }

    [Fact]
    public void Email_e_normalizado_para_minuscula()
    {
        var company = Normalize(b => b.Fields = b.Fields with { Email = "  Contato@VentoSul.COM.BR " });

        Assert.Equal("contato@ventosul.com.br", company.Email);
    }

    [Fact]
    public void Matriz_declarada_pela_receita_e_preservada()
    {
        // Redundante com IsHeadquarters, e nao por descuido: o derivado do CNPJ
        // vale para qualquer fonte, este e o que a autoridade cadastral afirmou.
        var company = Normalize(b => b.Fields = b.Fields with { MatrizFilial = "1" });

        Assert.Equal("1", company.MatrizFilial);
        Assert.True(company.IsHeadquarters);
    }

    [Fact]
    public void Municipio_por_codigo_e_por_nome_convivem()
    {
        var company = Normalize(b => b.Fields = b.Fields with
        {
            Municipio = "BAURU",
            MunicipioCodigo = "6219"
        });

        // O nome vem do join com Municipios.zip; o codigo fica para reconciliar
        // com a fonte quando o nome divergir.
        Assert.Equal("Bauru", company.Municipio);
        Assert.Equal("6219", company.MunicipioCodigo);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("02")]
    [InlineData("ATIVA")]
    [InlineData(null)]
    [InlineData("")]
    public void Situacao_ativa_e_reconhecida_nas_grafias_da_fonte(string? situacao)
    {
        // O mesmo predicado governa o filtro na origem da carga. Duas listas de
        // situacoes ativas divergiriam no primeiro codigo novo publicado.
        Assert.True(CompanyNormalizer.IsActiveRegistration(situacao));
    }

    [Theory]
    [InlineData("08")]
    [InlineData("3")]
    [InlineData("BAIXADA")]
    public void Situacao_nao_ativa_e_recusada(string situacao)
    {
        Assert.False(CompanyNormalizer.IsActiveRegistration(situacao));
    }
}
