namespace AutoHous.Revenue.Application;

/// <summary>
/// Uma linha do arquivo <c>Estabelecimentos</c>. Os nomes seguem o layout
/// oficial da Receita porque e dele que o dado vem; traduzir na borda so criaria
/// um dicionario a mais para manter quando o layout mudar.
/// </summary>
public sealed record ReceitaEstabelecimento
{
    /// <summary>Oito primeiros digitos do CNPJ. Identidade da empresa, nao do estabelecimento.</summary>
    public required string CnpjBasico { get; init; }
    public required string CnpjOrdem { get; init; }
    public required string CnpjDv { get; init; }

    /// <summary>1 = matriz, 2 = filial.</summary>
    public string? MatrizFilial { get; init; }
    public string? NomeFantasia { get; init; }
    public string? SituacaoCadastral { get; init; }
    public string? DataSituacaoCadastral { get; init; }
    public string? MotivoSituacaoCadastral { get; init; }
    public string? DataInicioAtividade { get; init; }
    public string? CnaePrincipal { get; init; }
    public string? CnaesSecundarios { get; init; }
    public string? TipoLogradouro { get; init; }
    public string? Logradouro { get; init; }
    public string? Numero { get; init; }
    public string? Complemento { get; init; }
    public string? Bairro { get; init; }
    public string? Cep { get; init; }
    public string? Uf { get; init; }

    /// <summary>Codigo proprio da RF, de quatro digitos. Nao e IBGE.</summary>
    public string? MunicipioCodigo { get; init; }
    public string? Ddd1 { get; init; }
    public string? Telefone1 { get; init; }
    public string? Ddd2 { get; init; }
    public string? Telefone2 { get; init; }
    public string? Email { get; init; }

    /// <summary>CNPJ de 14 digitos, remontado das tres partes.</summary>
    public string Cnpj => $"{CnpjBasico}{CnpjOrdem}{CnpjDv}";
}

/// <summary>Uma linha do arquivo <c>Empresas</c>, chaveada pela raiz do CNPJ.</summary>
public sealed record ReceitaEmpresa
{
    public required string CnpjBasico { get; init; }
    public string? RazaoSocial { get; init; }
    public string? NaturezaJuridica { get; init; }
    public string? QualificacaoResponsavel { get; init; }
    public string? CapitalSocial { get; init; }
    public string? Porte { get; init; }
    public string? EnteFederativoResponsavel { get; init; }
}

/// <summary>Uma linha do arquivo <c>Simples</c>.</summary>
public sealed record ReceitaSimples
{
    public required string CnpjBasico { get; init; }
    public string? OpcaoSimples { get; init; }
    public string? DataOpcaoSimples { get; init; }
    public string? DataExclusaoSimples { get; init; }
    public string? OpcaoMei { get; init; }
    public string? DataOpcaoMei { get; init; }
    public string? DataExclusaoMei { get; init; }
}

/// <summary>Uma linha do arquivo <c>Socios</c>. O CPF ja vem mascarado da origem.</summary>
public sealed record ReceitaSocio
{
    public required string CnpjBasico { get; init; }
    public string? Identificador { get; init; }
    public string? Nome { get; init; }
    public string? CpfCnpj { get; init; }
    public string? Qualificacao { get; init; }
    public string? DataEntrada { get; init; }
    public string? Pais { get; init; }
    public string? RepresentanteCpf { get; init; }
    public string? RepresentanteNome { get; init; }
    public string? RepresentanteQualificacao { get; init; }
    public string? FaixaEtaria { get; init; }
}

/// <summary>
/// Tabelas de dominio do release: codigo para descricao.
///
/// <see cref="Municipios"/> nao e conveniencia: o arquivo de estabelecimentos
/// grava o municipio como codigo proprio da RF de quatro digitos, e sem este
/// join <c>companies_cnpj.municipio</c> receberia "6219" em vez de "Bauru" - o
/// que quebraria a busca por cidade e a regra de mesma UF do account graph.
/// </summary>
public sealed record ReceitaDomainTables
{
    public IReadOnlyDictionary<string, string> Cnaes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Municipios { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Naturezas { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Motivos { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Paises { get; init; } = new Dictionary<string, string>();
}

/// <summary>Que arquivos do release a carga precisa. Socios e opt-in por ser PII.</summary>
[Flags]
public enum ReceitaFileSet
{
    DomainTables = 1,
    Estabelecimentos = 2,
    Empresas = 4,
    Simples = 8,
    Socios = 16,

    /// <summary>O minimo para produzir uma linha utilizavel.</summary>
    Minimum = DomainTables | Estabelecimentos | Empresas,

    Default = Minimum | Simples
}

/// <summary>
/// Leitura em stream de um release da Receita.
///
/// Porta, e nao classe: o adaptador abre zip, decodifica ISO-8859-1 e conhece a
/// posicao de cada campo no layout oficial. Nada disso e regra de negocio, e
/// tudo isso e o que impede o caso de uso de carga de ser testavel sem 7 GB de
/// arquivo.
///
/// Todo metodo devolve <c>IAsyncEnumerable</c> porque nenhum destes arquivos cabe
/// em memoria: <c>Estabelecimentos0.zip</c> sozinho descomprime para cerca de
/// 10 GB.
/// </summary>
public interface IReceitaSourceReader
{
    /// <summary>
    /// Garante que os arquivos do conjunto estao disponiveis localmente e
    /// devolve o digest de cada um. Baixa o que faltar, retomando o que estiver
    /// parcial.
    /// </summary>
    Task<IReadOnlyList<ReceitaFileDigest>> EnsureLocalAsync(
        string release, ReceitaFileSet files, CancellationToken ct = default);

    Task<ReceitaDomainTables> ReadDomainTablesAsync(string release, CancellationToken ct = default);

    IAsyncEnumerable<ReceitaEstabelecimento> ReadEstabelecimentosAsync(
        string release, CancellationToken ct = default);

    IAsyncEnumerable<ReceitaEmpresa> ReadEmpresasAsync(string release, CancellationToken ct = default);

    IAsyncEnumerable<ReceitaSimples> ReadSimplesAsync(string release, CancellationToken ct = default);

    IAsyncEnumerable<ReceitaSocio> ReadSociosAsync(string release, CancellationToken ct = default);
}
