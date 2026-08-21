using AutoHous.Revenue.Application;

namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>
/// O layout oficial dos Dados Abertos CNPJ, campo a campo.
///
/// Os arquivos NAO tem cabecalho: a unica coisa que diz o que e cada coluna e a
/// posicao, publicada em <c>cnpj-metadados.pdf</c>. Por isso as posicoes estao
/// nomeadas aqui em vez de aparecerem como <c>fields[11]</c> espalhados pelo
/// codigo - trocar a ordem de duas colunas silenciosamente e o defeito mais caro
/// que esta fonte pode produzir, porque o resultado continua parecendo dado
/// valido.
/// </summary>
public static class ReceitaLayout
{
    // -------------------------------------------------------------- EMPRESAS
    private const int EmpCnpjBasico = 0;
    private const int EmpRazaoSocial = 1;
    private const int EmpNaturezaJuridica = 2;
    private const int EmpQualificacaoResponsavel = 3;
    private const int EmpCapitalSocial = 4;
    private const int EmpPorte = 5;
    private const int EmpEnteFederativo = 6;

    public const int EmpresaFieldCount = 7;

    // ------------------------------------------------------ ESTABELECIMENTOS
    private const int EstCnpjBasico = 0;
    private const int EstCnpjOrdem = 1;
    private const int EstCnpjDv = 2;
    private const int EstMatrizFilial = 3;
    private const int EstNomeFantasia = 4;
    private const int EstSituacaoCadastral = 5;
    private const int EstDataSituacaoCadastral = 6;
    private const int EstMotivoSituacaoCadastral = 7;
    private const int EstNomeCidadeExterior = 8;
    private const int EstPais = 9;
    private const int EstDataInicioAtividade = 10;
    private const int EstCnaePrincipal = 11;
    private const int EstCnaesSecundarios = 12;
    private const int EstTipoLogradouro = 13;
    private const int EstLogradouro = 14;
    private const int EstNumero = 15;
    private const int EstComplemento = 16;
    private const int EstBairro = 17;
    private const int EstCep = 18;
    private const int EstUf = 19;
    private const int EstMunicipio = 20;
    private const int EstDdd1 = 21;
    private const int EstTelefone1 = 22;
    private const int EstDdd2 = 23;
    private const int EstTelefone2 = 24;
    private const int EstDddFax = 25;
    private const int EstFax = 26;
    private const int EstEmail = 27;
    private const int EstSituacaoEspecial = 28;
    private const int EstDataSituacaoEspecial = 29;

    public const int EstabelecimentoFieldCount = 30;

    // --------------------------------------------------------------- SIMPLES
    private const int SimCnpjBasico = 0;
    private const int SimOpcaoSimples = 1;
    private const int SimDataOpcaoSimples = 2;
    private const int SimDataExclusaoSimples = 3;
    private const int SimOpcaoMei = 4;
    private const int SimDataOpcaoMei = 5;
    private const int SimDataExclusaoMei = 6;

    public const int SimplesFieldCount = 7;

    // ---------------------------------------------------------------- SOCIOS
    private const int SocCnpjBasico = 0;
    private const int SocIdentificador = 1;
    private const int SocNome = 2;
    private const int SocCpfCnpj = 3;
    private const int SocQualificacao = 4;
    private const int SocDataEntrada = 5;
    private const int SocPais = 6;
    private const int SocRepresentanteCpf = 7;
    private const int SocRepresentanteNome = 8;
    private const int SocRepresentanteQualificacao = 9;
    private const int SocFaixaEtaria = 10;

    public const int SocioFieldCount = 11;

    // ------------------------------------------------------------ mapeamentos

    public static ReceitaEmpresa? ToEmpresa(string[] f)
    {
        var basico = Basico(f, EmpCnpjBasico);
        if (basico is null) return null;

        return new ReceitaEmpresa
        {
            CnpjBasico = basico,
            RazaoSocial = At(f, EmpRazaoSocial),
            NaturezaJuridica = At(f, EmpNaturezaJuridica),
            QualificacaoResponsavel = At(f, EmpQualificacaoResponsavel),
            CapitalSocial = At(f, EmpCapitalSocial),
            Porte = At(f, EmpPorte),
            EnteFederativoResponsavel = At(f, EmpEnteFederativo)
        };
    }

    public static ReceitaEstabelecimento? ToEstabelecimento(string[] f)
    {
        var basico = Basico(f, EstCnpjBasico);
        if (basico is null) return null;

        return new ReceitaEstabelecimento
        {
            CnpjBasico = basico,
            // Ordem e DV compoem o CNPJ de 14 digitos. Zeros a esquerda sao
            // significativos: "1" e a ordem 0001, e concatenar sem preencher
            // produziria um CNPJ de 11 digitos que nao existe.
            CnpjOrdem = Pad(At(f, EstCnpjOrdem), 4),
            CnpjDv = Pad(At(f, EstCnpjDv), 2),
            MatrizFilial = At(f, EstMatrizFilial),
            NomeFantasia = At(f, EstNomeFantasia),
            SituacaoCadastral = At(f, EstSituacaoCadastral),
            DataSituacaoCadastral = At(f, EstDataSituacaoCadastral),
            MotivoSituacaoCadastral = At(f, EstMotivoSituacaoCadastral),
            DataInicioAtividade = At(f, EstDataInicioAtividade),
            CnaePrincipal = At(f, EstCnaePrincipal),
            CnaesSecundarios = At(f, EstCnaesSecundarios),
            TipoLogradouro = At(f, EstTipoLogradouro),
            Logradouro = At(f, EstLogradouro),
            Numero = At(f, EstNumero),
            Complemento = At(f, EstComplemento),
            Bairro = At(f, EstBairro),
            Cep = At(f, EstCep),
            Uf = At(f, EstUf),
            MunicipioCodigo = At(f, EstMunicipio),
            Ddd1 = At(f, EstDdd1),
            Telefone1 = At(f, EstTelefone1),
            Ddd2 = At(f, EstDdd2),
            Telefone2 = At(f, EstTelefone2),
            Email = At(f, EstEmail)
        };
    }

    public static ReceitaSimples? ToSimples(string[] f)
    {
        var basico = Basico(f, SimCnpjBasico);
        if (basico is null) return null;

        return new ReceitaSimples
        {
            CnpjBasico = basico,
            OpcaoSimples = At(f, SimOpcaoSimples),
            DataOpcaoSimples = At(f, SimDataOpcaoSimples),
            DataExclusaoSimples = At(f, SimDataExclusaoSimples),
            OpcaoMei = At(f, SimOpcaoMei),
            DataOpcaoMei = At(f, SimDataOpcaoMei),
            DataExclusaoMei = At(f, SimDataExclusaoMei)
        };
    }

    public static ReceitaSocio? ToSocio(string[] f)
    {
        var basico = Basico(f, SocCnpjBasico);
        if (basico is null) return null;

        return new ReceitaSocio
        {
            CnpjBasico = basico,
            Identificador = At(f, SocIdentificador),
            Nome = At(f, SocNome),
            CpfCnpj = At(f, SocCpfCnpj),
            Qualificacao = At(f, SocQualificacao),
            DataEntrada = At(f, SocDataEntrada),
            Pais = At(f, SocPais),
            RepresentanteCpf = At(f, SocRepresentanteCpf),
            RepresentanteNome = At(f, SocRepresentanteNome),
            RepresentanteQualificacao = At(f, SocRepresentanteQualificacao),
            FaixaEtaria = At(f, SocFaixaEtaria)
        };
    }

    /// <summary>Tabela de dominio: <c>codigo;descricao</c>, duas colunas.</summary>
    public static (string Code, string Description)? ToDomainEntry(string[] f)
    {
        var code = At(f, 0);
        return code is null ? null : (code, At(f, 1) ?? code);
    }

    private static string? At(string[] fields, int index) => QuotedDelimitedLine.At(fields, index);

    /// <summary>
    /// Raiz do CNPJ com oito digitos. Linha sem raiz legivel nao e recuperavel -
    /// ela nao pertence a nenhuma empresa - e por isso vira <c>null</c> aqui em
    /// vez de seguir e falhar mais adiante.
    /// </summary>
    private static string? Basico(string[] fields, int index)
    {
        var raw = At(fields, index);
        if (raw is null) return null;

        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);

        return digits.Length is > 0 and <= 8 ? digits.PadLeft(8, '0') : null;
    }

    private static string Pad(string? value, int length)
    {
        var digits = new string([.. (value ?? string.Empty).Where(char.IsAsciiDigit)]);

        return digits.Length >= length ? digits[^length..] : digits.PadLeft(length, '0');
    }
}
