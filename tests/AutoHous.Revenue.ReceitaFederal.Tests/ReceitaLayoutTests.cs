namespace AutoHous.Revenue.ReceitaFederal.Tests;

/// <summary>
/// O layout posicional oficial. Estes testes existem porque os arquivos NAO tem
/// cabecalho: trocar duas colunas de lugar continuaria produzindo dado com cara
/// de valido, e o defeito so apareceria semanas depois, num e-mail para o
/// cliente errado.
/// </summary>
public class ReceitaLayoutTests
{
    /// <summary>Linha de estabelecimento com o valor de cada campo igual a sua posicao.</summary>
    private static string[] PositionalEstabelecimento()
    {
        var fields = new string[ReceitaLayout.EstabelecimentoFieldCount];

        for (var i = 0; i < fields.Length; i++) fields[i] = $"campo{i}";

        fields[0] = "11222333";
        fields[1] = "0001";
        fields[2] = "81";

        return fields;
    }

    [Fact]
    public void Estabelecimento_le_cada_campo_da_posicao_certa()
    {
        var est = ReceitaLayout.ToEstabelecimento(PositionalEstabelecimento())!;

        Assert.Equal("campo3", est.MatrizFilial);
        Assert.Equal("campo4", est.NomeFantasia);
        Assert.Equal("campo5", est.SituacaoCadastral);
        Assert.Equal("campo7", est.MotivoSituacaoCadastral);
        Assert.Equal("campo10", est.DataInicioAtividade);
        Assert.Equal("campo11", est.CnaePrincipal);
        Assert.Equal("campo12", est.CnaesSecundarios);
        Assert.Equal("campo18", est.Cep);
        Assert.Equal("campo19", est.Uf);
        Assert.Equal("campo20", est.MunicipioCodigo);
        Assert.Equal("campo22", est.Telefone1);
        // Posicao 27 e o e-mail; 25 e 26 sao DDD do fax e fax, que nao guardamos.
        Assert.Equal("campo27", est.Email);
    }

    [Fact]
    public void Cnpj_e_remontado_das_tres_partes()
    {
        var est = ReceitaLayout.ToEstabelecimento(PositionalEstabelecimento())!;

        Assert.Equal("11222333000181", est.Cnpj);
        Assert.Equal(14, est.Cnpj.Length);
    }

    [Fact]
    public void Zeros_a_esquerda_da_ordem_e_do_dv_sao_restaurados()
    {
        // Uma ferramenta que trate ordem como numero entrega "1" no lugar de
        // "0001". Concatenar sem preencher produziria um CNPJ de 11 digitos que
        // nao existe - e que passaria pelo digito verificador como invalido, sem
        // ninguem entender por que.
        var fields = PositionalEstabelecimento();
        fields[1] = "1";
        fields[2] = "5";

        var est = ReceitaLayout.ToEstabelecimento(fields)!;

        Assert.Equal("11222333000105", est.Cnpj);
    }

    [Fact]
    public void Raiz_curta_e_preenchida_para_oito_digitos()
    {
        var fields = PositionalEstabelecimento();
        fields[0] = "222333";

        Assert.Equal("00222333", ReceitaLayout.ToEstabelecimento(fields)!.CnpjBasico);
    }

    [Fact]
    public void Linha_sem_raiz_legivel_nao_vira_registro()
    {
        // Ela nao pertence a empresa nenhuma. Virar null aqui e o que permite ao
        // leitor conta-la e reportar, em vez de falhar mais adiante sem contexto.
        var fields = PositionalEstabelecimento();
        fields[0] = "";

        Assert.Null(ReceitaLayout.ToEstabelecimento(fields));
    }

    [Fact]
    public void Empresa_le_cada_campo_da_posicao_certa()
    {
        var fields = new string[ReceitaLayout.EmpresaFieldCount];
        for (var i = 0; i < fields.Length; i++) fields[i] = $"campo{i}";
        fields[0] = "11222333";

        var empresa = ReceitaLayout.ToEmpresa(fields)!;

        Assert.Equal("11222333", empresa.CnpjBasico);
        Assert.Equal("campo1", empresa.RazaoSocial);
        Assert.Equal("campo2", empresa.NaturezaJuridica);
        Assert.Equal("campo4", empresa.CapitalSocial);
        Assert.Equal("campo5", empresa.Porte);
    }

    [Fact]
    public void Simples_le_cada_campo_da_posicao_certa()
    {
        var fields = new string[ReceitaLayout.SimplesFieldCount];
        for (var i = 0; i < fields.Length; i++) fields[i] = $"campo{i}";
        fields[0] = "11222333";
        fields[1] = "S";
        fields[4] = "N";

        var simples = ReceitaLayout.ToSimples(fields)!;

        Assert.Equal("S", simples.OpcaoSimples);
        Assert.Equal("N", simples.OpcaoMei);
    }

    [Fact]
    public void Socio_le_cada_campo_da_posicao_certa()
    {
        var fields = new string[ReceitaLayout.SocioFieldCount];
        for (var i = 0; i < fields.Length; i++) fields[i] = $"campo{i}";
        fields[0] = "11222333";
        fields[3] = "***456789**";

        var socio = ReceitaLayout.ToSocio(fields)!;

        Assert.Equal("campo1", socio.Identificador);
        Assert.Equal("campo2", socio.Nome);
        // O CPF ja chega mascarado da origem e e guardado como veio.
        Assert.Equal("***456789**", socio.CpfCnpj);
        Assert.Equal("campo5", socio.DataEntrada);
        Assert.Equal("campo10", socio.FaixaEtaria);
    }

    [Fact]
    public void Linha_truncada_nao_derruba_o_mapeamento()
    {
        // Arquivo publico tem linha defeituosa. Perder as colunas do fim e
        // aceitavel; perder a carga inteira nao.
        var est = ReceitaLayout.ToEstabelecimento(["11222333", "0001", "81", "1", "VENTO SUL"])!;

        Assert.Equal("11222333000181", est.Cnpj);
        Assert.Equal("VENTO SUL", est.NomeFantasia);
        Assert.Null(est.CnaePrincipal);
        Assert.Null(est.Uf);
    }

    [Fact]
    public void Tabela_de_dominio_e_codigo_e_descricao()
    {
        var entry = ReceitaLayout.ToDomainEntry(["6219", "BAURU"])!.Value;

        Assert.Equal("6219", entry.Code);
        Assert.Equal("BAURU", entry.Description);
    }

    [Fact]
    public void Tabela_de_dominio_sem_descricao_repete_o_codigo()
    {
        var entry = ReceitaLayout.ToDomainEntry(["9999", ""])!.Value;

        Assert.Equal("9999", entry.Description);
    }
}
