using System.Globalization;

namespace AutoHous.Revenue.Domain;

/// <summary>
/// Linha de origem, exatamente como veio da fonte.
///
/// Os sete primeiros campos sao o minimo que qualquer extrato empresarial traz.
/// O resto so aparece na base oficial da Receita Federal, e e opcional por isso:
/// uma lista de CNPJs colada de uma planilha continua sendo entrada valida.
/// </summary>
public sealed record RawCompanyFields
{
    public string? Cnpj { get; init; }
    public string? RazaoSocial { get; init; }
    public string? NomeFantasia { get; init; }
    public string? CnaePrincipal { get; init; }
    public string? SituacaoCadastral { get; init; }
    public string? Municipio { get; init; }
    public string? Uf { get; init; }

    // --------------------------------------------------- so na base da Receita
    /// <summary>1 = matriz, 2 = filial.</summary>
    public string? MatrizFilial { get; init; }
    public string? NaturezaJuridica { get; init; }
    public string? Porte { get; init; }
    public string? CapitalSocial { get; init; }
    public string? DataInicioAtividade { get; init; }
    public string? DataSituacaoCadastral { get; init; }
    public string? MotivoSituacaoCadastral { get; init; }
    /// <summary>Lista separada por virgula, no formato da RF.</summary>
    public string? CnaesSecundarios { get; init; }
    /// <summary>Codigo proprio da RF, de quatro digitos - nao IBGE.</summary>
    public string? MunicipioCodigo { get; init; }
    public string? Cep { get; init; }
    public string? Logradouro { get; init; }
    public string? Numero { get; init; }
    public string? Complemento { get; init; }
    public string? Bairro { get; init; }
    public string? Telefone1 { get; init; }
    public string? Telefone2 { get; init; }
    public string? Email { get; init; }
    public string? OpcaoSimples { get; init; }
    public string? OpcaoMei { get; init; }
}

/// <summary>Empresa aprovada na normalizacao, pronta para virar (ou entrar em) uma account.</summary>
public sealed record NormalizedCompany
{
    public required string Cnpj { get; init; }

    /// <summary>Oito primeiros digitos: identidade da matriz. Filiais compartilham a raiz.</summary>
    public required string CnpjRoot { get; init; }

    public required string RazaoSocial { get; init; }
    public string? NomeFantasia { get; init; }

    /// <summary>Nome comercial preferido: fantasia quando existe, razao social caso contrario.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Forma canonica para casamento difuso (<see cref="NameNormalizer"/>).</summary>
    public required string NormalizedName { get; init; }

    public required CnaeClassification Cnae { get; init; }
    public string? Municipio { get; init; }
    public string? Uf { get; init; }
    public string? SituacaoCadastral { get; init; }

    // ------------------------------------------------ cadastro da fonte oficial
    /// <summary>
    /// 1 = matriz, 2 = filial, como a Receita declara. Redundante com
    /// <see cref="IsHeadquarters"/>, e nao por descuido: o derivado do CNPJ vale
    /// para qualquer fonte, e este e o que a autoridade cadastral afirmou.
    /// </summary>
    public string? MatrizFilial { get; init; }
    public string? NaturezaJuridica { get; init; }
    public string? Porte { get; init; }
    public decimal? CapitalSocial { get; init; }
    public DateOnly? DataAbertura { get; init; }
    public DateOnly? DataSituacaoCadastral { get; init; }
    public string? MotivoSituacaoCadastral { get; init; }
    public IReadOnlyList<string> CnaesSecundarios { get; init; } = [];
    public string? MunicipioCodigo { get; init; }

    // --------------------------------------------------------------- endereco
    public string? Cep { get; init; }
    public string? Logradouro { get; init; }
    public string? Numero { get; init; }
    public string? Complemento { get; init; }
    public string? Bairro { get; init; }

    /// <summary>
    /// Contato DA PESSOA JURIDICA, publicado pela propria Receita. Nao e PII de
    /// pessoa fisica e nao cria <c>contacts</c> - quem faz isso e o People
    /// Finder, sob a politica do frame 09.
    /// </summary>
    public string? Telefone1 { get; init; }
    public string? Telefone2 { get; init; }
    public string? Email { get; init; }

    /// <summary>S / N / vazio. Sinal de porte mais confiavel que <see cref="Porte"/>.</summary>
    public string? OpcaoSimples { get; init; }
    public string? OpcaoMei { get; init; }

    /// <summary>Matriz quando a ordem da filial (digitos 9-12) e 0001.</summary>
    public bool IsHeadquarters => Cnpj.Substring(8, 4) == "0001";
}

public enum CompanyRejectionReason
{
    None,
    InvalidCnpj,
    MissingName,
    UnknownCnae,
    OutsideUniverse,
    InactiveRegistration,
    InvalidUf
}

public sealed record CompanyNormalizationResult(
    NormalizedCompany? Company,
    CompanyRejectionReason Reason)
{
    public bool Accepted => Company is not null;

    public static CompanyNormalizationResult Ok(NormalizedCompany company) => new(company, CompanyRejectionReason.None);
    public static CompanyNormalizationResult Reject(CompanyRejectionReason reason) => new(null, reason);

    /// <summary>Rotulo gravado em <c>companies_raw.rejection_reason</c>.</summary>
    public string ReasonLabel => Reason switch
    {
        CompanyRejectionReason.None => "accepted",
        CompanyRejectionReason.InvalidCnpj => "invalid_cnpj",
        CompanyRejectionReason.MissingName => "missing_name",
        CompanyRejectionReason.UnknownCnae => "unknown_cnae",
        CompanyRejectionReason.OutsideUniverse => "outside_universe",
        CompanyRejectionReason.InactiveRegistration => "inactive_registration",
        CompanyRejectionReason.InvalidUf => "invalid_uf",
        _ => "unknown"
    };
}

/// <summary>
/// Etapa 02 do pipeline de captura: transforma uma linha crua em uma empresa
/// utilizavel, ou explica por que ela nao serve.
///
/// A funcao e pura de proposito. Reprocessar o mesmo <c>companies_raw</c> depois
/// de corrigir uma regra produz o mesmo resultado sem tocar na fonte - que e o
/// motivo de existir o estagio de linha crua.
/// </summary>
public static class CompanyNormalizer
{
    private static readonly HashSet<string> BrazilianStates =
    [
        "AC","AL","AP","AM","BA","CE","DF","ES","GO","MA","MT","MS","MG","PA","PB",
        "PR","PE","PI","RJ","RN","RS","RO","RR","SC","SP","SE","TO"
    ];

    /// <summary>
    /// Situacoes que valem prospeccao. A base da Receita usa o codigo "02" para
    /// ativa; alguns extratos trazem o rotulo por extenso.
    /// </summary>
    private static readonly HashSet<string> ActiveRegistrations =
        new(StringComparer.OrdinalIgnoreCase) { "02", "2", "ATIVA", "ACTIVE" };

    /// <summary>
    /// Se a situacao cadastral vale prospeccao. Campo em branco conta como ativa:
    /// extrato que nao traz a coluna nao deve reprovar a base inteira.
    ///
    /// Publico porque o filtro na origem da carga da Receita precisa da MESMA
    /// resposta que a normalizacao. Duas listas de situacoes ativas em lugares
    /// diferentes divergiriam na primeira vez que a Receita publicasse um codigo
    /// novo - e a divergencia apareceria como linha capturada e depois rejeitada,
    /// sem ninguem entender por que.
    /// </summary>
    public static bool IsActiveRegistration(string? situacaoCadastral)
    {
        var situacao = Collapse(situacaoCadastral);

        return string.IsNullOrEmpty(situacao) || ActiveRegistrations.Contains(situacao);
    }

    public static CompanyNormalizationResult Normalize(
        RawCompanyFields raw, bool requireCoreIcp = false)
    {
        if (!CnpjNormalizer.TryNormalize(raw.Cnpj, out var cnpj))
        {
            return CompanyNormalizationResult.Reject(CompanyRejectionReason.InvalidCnpj);
        }

        var razaoSocial = Collapse(raw.RazaoSocial);
        var nomeFantasia = Collapse(raw.NomeFantasia);

        if (string.IsNullOrEmpty(razaoSocial) && string.IsNullOrEmpty(nomeFantasia))
        {
            return CompanyNormalizationResult.Reject(CompanyRejectionReason.MissingName);
        }

        var cnae = CnaeCatalog.Classify(raw.CnaePrincipal);

        if (cnae is null)
        {
            // Distingue "codigo ilegivel" de "codigo valido fora do universo":
            // o primeiro e defeito de parsing e merece investigacao, o segundo e
            // funcionamento normal do filtro.
            return CompanyNormalizationResult.Reject(
                CnaeCatalog.NormalizeCode(raw.CnaePrincipal) is null
                    ? CompanyRejectionReason.UnknownCnae
                    : CompanyRejectionReason.OutsideUniverse);
        }

        if (requireCoreIcp && !cnae.InCoreIcp)
        {
            return CompanyNormalizationResult.Reject(CompanyRejectionReason.OutsideUniverse);
        }

        var situacao = Collapse(raw.SituacaoCadastral);

        if (!IsActiveRegistration(situacao))
        {
            return CompanyNormalizationResult.Reject(CompanyRejectionReason.InactiveRegistration);
        }

        var uf = Collapse(raw.Uf)?.ToUpperInvariant();

        if (!string.IsNullOrEmpty(uf) && !BrazilianStates.Contains(uf))
        {
            return CompanyNormalizationResult.Reject(CompanyRejectionReason.InvalidUf);
        }

        // Fantasia representa melhor como o mercado chama a empresa - e e o que o
        // agente vai procurar na web. Razao social fica como lastro cadastral.
        var displayName = !string.IsNullOrEmpty(nomeFantasia) ? nomeFantasia : razaoSocial!;

        return CompanyNormalizationResult.Ok(new NormalizedCompany
        {
            Cnpj = cnpj,
            CnpjRoot = cnpj[..8],
            RazaoSocial = razaoSocial ?? displayName,
            NomeFantasia = nomeFantasia,
            // O sufixo societario sai do nome de exibicao e fica na razao social:
            // accounts.name e identidade comercial, companies_cnpj.razao_social e
            // o registro legal.
            DisplayName = TitleCase(NameNormalizer.StripLegalSuffix(displayName))!,
            NormalizedName = NameNormalizer.Normalize(displayName),
            Cnae = cnae,
            Municipio = TitleCase(Collapse(raw.Municipio)),
            Uf = uf,
            SituacaoCadastral = situacao,

            MatrizFilial = Collapse(raw.MatrizFilial),
            NaturezaJuridica = Collapse(raw.NaturezaJuridica),
            Porte = Collapse(raw.Porte),
            CapitalSocial = ParseDecimal(raw.CapitalSocial),
            DataAbertura = ParseDate(raw.DataInicioAtividade),
            DataSituacaoCadastral = ParseDate(raw.DataSituacaoCadastral),
            MotivoSituacaoCadastral = Collapse(raw.MotivoSituacaoCadastral),
            CnaesSecundarios = ParseCnaeList(raw.CnaesSecundarios),
            MunicipioCodigo = Collapse(raw.MunicipioCodigo),

            Cep = Digits(raw.Cep),
            Logradouro = TitleCase(Collapse(raw.Logradouro)),
            Numero = Collapse(raw.Numero),
            Complemento = TitleCase(Collapse(raw.Complemento)),
            Bairro = TitleCase(Collapse(raw.Bairro)),

            Telefone1 = Digits(raw.Telefone1),
            Telefone2 = Digits(raw.Telefone2),
            Email = Collapse(raw.Email)?.ToLowerInvariant(),

            OpcaoSimples = Collapse(raw.OpcaoSimples)?.ToUpperInvariant(),
            OpcaoMei = Collapse(raw.OpcaoMei)?.ToUpperInvariant()
        });
    }

    /// <summary>
    /// A Receita escreve data como <c>AAAAMMDD</c> e escreve "sem data" de
    /// varias maneiras: <c>0</c>, <c>00000000</c>, campo em branco. Todas viram
    /// <c>null</c> - gravar 01/01/0001 como data de abertura poluiria qualquer
    /// calculo de idade da empresa com uma data que parece real.
    /// </summary>
    private static DateOnly? ParseDate(string? raw)
    {
        var digits = Digits(raw);

        if (digits is null || digits.Length != 8) return null;

        return DateOnly.TryParseExact(
            digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// Capital social vem com virgula decimal. Interpretar com a cultura da
    /// maquina faria "1000,00" virar cem mil em um servidor com locale ingles -
    /// erro de tres ordens de grandeza que passa despercebido.
    /// </summary>
    private static decimal? ParseDecimal(string? raw)
    {
        var value = Collapse(raw);
        if (value is null) return null;

        var canonical = value.Replace(".", string.Empty).Replace(',', '.');

        return decimal.TryParse(
            canonical, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// CNAEs secundarios chegam separados por virgula. Normalizados um a um pelo
    /// mesmo caminho do principal, para que a mesma atividade nao apareca em duas
    /// grafias conforme a coluna de onde veio.
    /// </summary>
    private static IReadOnlyList<string> ParseCnaeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return
        [
            .. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(CnaeCatalog.NormalizeCode)
                  .Where(code => code is not null)
                  .Distinct(StringComparer.Ordinal)!
        ];
    }

    /// <summary>
    /// So os digitos. Telefone e CEP circulam com ponto, traco e parenteses; o
    /// que identifica os dois e o numero.
    /// </summary>
    private static string? Digits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);

        return digits.Length == 0 || digits.All(c => c == '0') ? null : digits;
    }

    private static string? Collapse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Bases publicas vem em caixa alta. "GRUPO VENTO SUL VEICULOS" dentro de um
    /// e-mail comercial parece grito; as preposicoes ficam minusculas.
    /// </summary>
    private static string? TitleCase(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var lower = value.ToLowerInvariant();
        var words = lower.Split(' ');
        var connectors = new HashSet<string> { "de", "da", "do", "das", "dos", "e", "em" };

        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Length == 0) continue;

            words[i] = i > 0 && connectors.Contains(words[i])
                ? words[i]
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words[i]);
        }

        return string.Join(' ', words);
    }
}
