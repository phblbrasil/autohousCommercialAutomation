namespace AutoHous.Revenue.ReceitaFederal.Tests;

public class QuotedDelimitedLineTests
{
    [Fact]
    public void Divide_campos_simples()
    {
        Assert.Equal(["11222333", "0001", "81"], QuotedDelimitedLine.Split("11222333;0001;81", ';'));
    }

    [Fact]
    public void Delimitador_dentro_de_aspas_nao_divide()
    {
        // Razao social com virgula e o caso comum, e o motivo de esta funcao
        // existir em vez de um String.Split.
        var fields = QuotedDelimitedLine.Split(
            "\"11222333\";\"COMERCIO DE VEICULOS SILVA, SANTOS E CIA LTDA\";\"SP\"", ';');

        Assert.Equal("COMERCIO DE VEICULOS SILVA, SANTOS E CIA LTDA", fields[1]);
        Assert.Equal(3, fields.Length);
    }

    [Fact]
    public void Aspas_duplicadas_dentro_do_campo_sao_literal()
    {
        var fields = QuotedDelimitedLine.Split("\"AUTO \"\"SUL\"\" LTDA\";\"SP\"", ';');

        Assert.Equal("AUTO \"SUL\" LTDA", fields[0]);
    }

    [Fact]
    public void Campo_vazio_e_preservado_na_posicao()
    {
        // Posicao e a unica coisa que diz o que e cada coluna num arquivo sem
        // cabecalho. Colapsar vazio deslocaria todas as colunas seguintes.
        var fields = QuotedDelimitedLine.Split("\"1\";\"\";\"3\"", ';');

        Assert.Equal(3, fields.Length);
        Assert.Equal(string.Empty, fields[1]);
    }

    [Fact]
    public void At_devolve_nulo_para_indice_fora_da_linha_e_para_campo_vazio()
    {
        var fields = QuotedDelimitedLine.Split("a;;c", ';');

        Assert.Equal("a", QuotedDelimitedLine.At(fields, 0));
        Assert.Null(QuotedDelimitedLine.At(fields, 1));
        Assert.Equal("c", QuotedDelimitedLine.At(fields, 2));

        // Linha truncada e realidade em arquivo publico: pedir a coluna 29 de uma
        // linha com 5 campos tem que devolver nada, e nao derrubar a carga.
        Assert.Null(QuotedDelimitedLine.At(fields, 29));
        Assert.Null(QuotedDelimitedLine.At(fields, -1));
    }
}
