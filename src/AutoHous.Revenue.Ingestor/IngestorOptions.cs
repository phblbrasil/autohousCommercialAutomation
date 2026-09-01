namespace AutoHous.Revenue.Ingestor;

/// <summary>Codigos de saida da CLI de captura. Um contrato, dois subcomandos.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int NoData = 1;
    public const int BadArguments = 2;

    /// <summary>Gravado, mas abaixo do quality gate de 85% de resolucao automatica.</summary>
    public const int QualityGate = 3;

    /// <summary>Fonte da Receita indisponivel, incompleta ou com layout inesperado.</summary>
    public const int SourceFailure = 4;
}

/// <summary>Opcoes do subcomando <c>arquivo</c> (o modo original).</summary>
internal sealed record IngestorOptions
{
    public required string Path { get; init; }
    public char Delimiter { get; init; } = ';';
    public string Encoding { get; init; } = "utf-8";
    public string? SourceName { get; init; }
    public bool DryRun { get; init; }

    public static IngestorOptions? Parse(string[] args)
    {
        if (args.Length == 0) return null;

        string? path = null, source = null, encoding = null;
        var delimiter = ';';
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file" or "-f" when i + 1 < args.Length:
                    path = args[++i];
                    break;

                case "--delimiter" or "-d" when i + 1 < args.Length:
                    var raw = args[++i];
                    delimiter = raw switch { "tab" or "\\t" => '\t', _ => raw[0] };
                    break;

                case "--encoding" or "-e" when i + 1 < args.Length:
                    encoding = args[++i];
                    break;

                case "--source" or "-s" when i + 1 < args.Length:
                    source = args[++i];
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                default:
                    if (!args[i].StartsWith('-') && path is null) path = args[i];
                    break;
            }
        }

        if (path is null || !File.Exists(path)) return null;

        return new IngestorOptions
        {
            Path = path,
            Delimiter = delimiter,
            // Extratos da Receita costumam vir em latin1; UTF-8 e o padrao
            // porque e o que sai de qualquer ferramenta moderna.
            Encoding = encoding ?? "utf-8",
            SourceName = source,
            DryRun = dryRun
        };
    }
}

/// <summary>Opcoes do subcomando <c>receita</c>: carga da fonte oficial.</summary>
internal sealed record ReceitaCommandOptions
{
    /// <summary>Lista as competencias publicadas e sai. Nao toca no banco.</summary>
    public bool List { get; init; }

    /// <summary>Competencia <c>AAAA-MM</c>. Vazio significa a mais recente publicada.</summary>
    public string? Release { get; init; }

    public bool StatsOnly { get; init; }
    public bool DryRun { get; init; }
    public long? Limit { get; init; }
    public bool IncludeSocios { get; init; }
    public bool IncludeInactive { get; init; }
    public bool IncludeSecondaryCnae { get; init; }
    public bool KeepSpool { get; init; }

    /// <summary>Le so o que ja esta no cache, sem consultar a Receita.</summary>
    public bool Offline { get; init; }

    public IReadOnlySet<string>? Ufs { get; init; }
    public string? CacheDir { get; init; }
    public string? WorkDir { get; init; }

    /// <summary>
    /// Retoma a resolucao do account graph de um lote JA CAPTURADO, pulando
    /// download, leitura e captura.
    ///
    /// Existe porque a carga nacional e longa e o que a interrompe raramente e
    /// o dado: na primeira execucao completa foi a maquina entrar em Modern
    /// Standby, matando as conexoes do pool. As linhas ficam em companies_raw
    /// com status 'pending' e o batch_id preservado - ou seja, o trabalho para
    /// retomar ja esta no banco, e so faltava um jeito de chamar
    /// ResolveAccountGraphUseCase sobre ele.
    ///
    /// Sem isto, a unica saida e reexecutar a carga inteira: 45 min relendo 72
    /// milhoes de estabelecimentos e um lote NOVO, que reinsere as mesmas linhas
    /// cruas em vez de aproveitar as existentes.
    /// </summary>
    public Guid? ResolveBatch { get; init; }

    public static ReceitaCommandOptions? Parse(string[] args)
    {
        string? release = null, cacheDir = null, workDir = null;
        HashSet<string>? ufs = null;
        long? limit = null;
        bool list = false, statsOnly = false, dryRun = false;
        bool socios = false, inativos = false, secundario = false, keepSpool = false, offline = false;
        Guid? resolveBatch = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list" or "-l":
                    list = true;
                    break;

                case "--release" or "-r" when i + 1 < args.Length:
                    release = args[++i];
                    break;

                case "--uf" when i + 1 < args.Length:
                    ufs = [.. args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(uf => uf.ToUpperInvariant())];
                    break;

                case "--limit" when i + 1 < args.Length:
                    if (!long.TryParse(args[++i], out var parsed) || parsed <= 0) return null;
                    limit = parsed;
                    break;

                case "--cache-dir" when i + 1 < args.Length:
                    cacheDir = args[++i];
                    break;

                case "--work-dir" when i + 1 < args.Length:
                    workDir = args[++i];
                    break;

                case "--stats-only":
                    statsOnly = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--socios":
                    socios = true;
                    break;

                case "--incluir-inativos":
                    inativos = true;
                    break;

                case "--incluir-cnae-secundario":
                    secundario = true;
                    break;

                case "--keep-spool":
                    keepSpool = true;
                    break;

                case "--offline":
                    offline = true;
                    break;

                case "--resolve-batch" when i + 1 < args.Length:
                    if (!Guid.TryParse(args[++i], out var batch)) return null;
                    resolveBatch = batch;
                    break;

                default:
                    if (!args[i].StartsWith('-') && release is null) release = args[i];
                    break;
            }
        }

        // Leitura parcial produz agregado parcial. Grava-lo como se fosse o
        // mercado seria pior que nao ter numero nenhum, entao o limite so existe
        // no ensaio.
        if (limit is not null && !dryRun) return null;

        if (release is not null && !IsRelease(release)) return null;

        // Retomada e um modo proprio: ela nao le a origem, nao toca no cache e
        // nao aceita recorte. Combinar com as flags de leitura daria a impressao
        // de que elas teriam efeito - e nao teriam, porque a leitura ja aconteceu.
        if (resolveBatch is not null &&
            (list || statsOnly || dryRun || offline || limit is not null ||
             ufs is not null || socios || release is not null))
        {
            return null;
        }

        // Sem consultar a origem nao existe "a competencia mais recente": em
        // modo offline o release tem de ser dito.
        if (offline && release is null && !list) return null;

        return new ReceitaCommandOptions
        {
            List = list,
            Release = release,
            StatsOnly = statsOnly,
            DryRun = dryRun,
            Limit = limit,
            IncludeSocios = socios,
            IncludeInactive = inativos,
            IncludeSecondaryCnae = secundario,
            KeepSpool = keepSpool,
            Offline = offline,
            ResolveBatch = resolveBatch,
            Ufs = ufs,
            CacheDir = cacheDir,
            WorkDir = workDir
        };
    }

    private static bool IsRelease(string value) =>
        value.Length == 7
        && value[4] == '-'
        && value[..4].All(char.IsAsciiDigit)
        && value[5..].All(char.IsAsciiDigit);
}
