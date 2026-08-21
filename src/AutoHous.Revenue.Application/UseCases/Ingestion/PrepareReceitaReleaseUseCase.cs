using System.Runtime.CompilerServices;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Application;

public sealed record PrepareReceitaReleaseCommand
{
    /// <summary>Competencia publicada pela Receita, no formato <c>AAAA-MM</c>.</summary>
    public required string Release { get; init; }

    /// <summary>
    /// Descarta na origem quem nao esta com situacao cadastral ativa.
    ///
    /// Ligado por padrao. Desligar dobra o volume capturado (a base traz o
    /// historico de baixadas e inaptas) em troca de poder investigar churn de
    /// revenda diretamente em <c>companies_raw</c>. Em qualquer dos dois modos, o
    /// inativo continua contado em <c>rf_cnae_stats</c>.
    /// </summary>
    public bool ActiveOnly { get; init; } = true;

    /// <summary>
    /// Admite quem tem CNAE do catalogo entre os secundarios. Pega a revenda
    /// registrada sob outro CNAE principal, ao custo de trazer quem so vende
    /// carro de vez em quando.
    /// </summary>
    public bool IncludeSecondaryCnae { get; init; }

    /// <summary>Recorte por UF. Vazio significa o pais inteiro.</summary>
    public IReadOnlySet<string>? Ufs { get; init; }

    public bool IncludeSocios { get; init; }

    /// <summary>Computa o agregado de mercado e nao captura empresa nenhuma.</summary>
    public bool StatsOnly { get; init; }

    /// <summary>
    /// Le e conta sem gravar nada - nem estatistica, nem lote, nem spool.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Para de ler depois de N estabelecimentos. So faz sentido com
    /// <see cref="DryRun"/>: uma leitura parcial produz agregado parcial, e
    /// gravar isso como se fosse o mercado seria pior que nao ter numero nenhum.
    /// </summary>
    public long? MaxEstablishments { get; init; }

    public int ChunkSize { get; init; } = 5_000;
}

public sealed record PrepareReceitaReleaseResult
{
    public required string Release { get; init; }

    /// <summary>Estabelecimentos lidos, antes de qualquer filtro.</summary>
    public required long EstablishmentsScanned { get; init; }

    /// <summary>Estabelecimentos que passaram no filtro de origem.</summary>
    public required long EstablishmentsSelected { get; init; }

    /// <summary>Raizes de CNPJ selecionadas que encontraram par no arquivo Empresas.</summary>
    public required long CompaniesJoined { get; init; }

    public required long PartnersLoaded { get; init; }

    public required IReadOnlyList<ReceitaFileDigest> Files { get; init; }

    /// <summary>
    /// O agregado, so em <c>DryRun</c> ou <c>StatsOnly</c>. Numa carga normal ele
    /// ja esta no banco, e devolve-lo aqui manteria centenas de milhares de
    /// registros vivos durante toda a ingestao, sem ninguem para le-los.
    /// </summary>
    public required IReadOnlyList<CnaeStatRow> Statistics { get; init; }

    /// <summary>
    /// As linhas prontas para <see cref="IngestCompanyStreamUseCase"/>, lidas do
    /// spool sob demanda: matrizes primeiro, filiais depois.
    /// </summary>
    public required IAsyncEnumerable<RawCompanyRow> Rows { get; init; }
}

/// <summary>
/// Camada 01 com a fonte oficial: transforma um release de Dados Abertos CNPJ da
/// Receita Federal nas linhas que o pipeline de captura ja sabe processar.
///
/// Quatro passadas, nesta ordem, porque a fonte impoe:
///
///   A. Estabelecimentos  conta TUDO para o agregado; guarda no spool o que
///                        pertence ao universo automotivo
///   B. Empresas          razao social, porte e capital - so das raizes que a
///                        passada A selecionou
///   C. Simples           opcao pelo Simples e MEI, mesmo recorte
///   D. Socios            quadro societario, mesmo recorte, so com opt-in
///
/// A ordem nao e escolha: razao social vive em Empresas, chaveada pela raiz do
/// CNPJ, e so depois de varrer os 5,1 GB de estabelecimentos se sabe quais
/// raizes interessam.
///
/// Este caso de uso NAO ingere e NAO resolve grafo. Ele para na linha pronta -
/// quem captura e <see cref="IngestCompanyStreamUseCase"/>, quem agrupa e
/// <see cref="ResolveAccountGraphUseCase"/>, exatamente como no caminho de
/// arquivo delimitado. Manter a fronteira e o que faz a fonte nova nao duplicar
/// nem a normalizacao nem o account graph.
/// </summary>
public sealed class PrepareReceitaReleaseUseCase(
    IReceitaSourceReader source,
    IReceitaSpool spool,
    IReceitaReleaseRepository releases,
    IMarketStatisticsRepository statistics,
    ICompanyPartnerRepository partners,
    IUnitOfWorkFactory unitOfWork,
    IIdentifierGenerator ids,
    ILogger<PrepareReceitaReleaseUseCase> logger)
{
    /// <summary>
    /// Dois spools em vez de um, e nao por organizacao: a passada A nao sabe
    /// ordenar 700 mil linhas sem carrega-las, e a matriz PRECISA entrar antes da
    /// filial. A regra 1 do <see cref="AccountGroupResolver"/> e raiz de CNPJ -
    /// com a matriz ja na base, a filial anexa por identidade, com confianca
    /// 1.00. Na ordem inversa, a filial cria a conta e a matriz chega depois
    /// disputando trigrama contra o nome da propria filial.
    /// </summary>
    private const string MatrizSpool = "matriz";
    private const string FilialSpool = "filial";

    public async Task<PrepareReceitaReleaseResult> ExecuteAsync(
        PrepareReceitaReleaseCommand command, CancellationToken ct = default)
    {
        if (command.MaxEstablishments is not null && !command.DryRun)
        {
            throw new ArgumentException(
                "MaxEstablishments so e valido em DryRun: leitura parcial produz agregado parcial, " +
                "e grava-lo como se fosse o mercado seria pior que nao ter numero nenhum.",
                nameof(command));
        }

        // O ensaio e o stats-only nao juntam nada: eles so contam o que
        // Estabelecimentos traz. Garantir Empresas, Simples e Socios ali seria
        // baixar 2,2 GB para nao ler nenhum deles.
        var fileSet = command.DryRun || command.StatsOnly
            ? ReceitaFileSet.DomainTables | ReceitaFileSet.Estabelecimentos
            : ReceitaFileSet.Minimum
              | ReceitaFileSet.Simples
              | (command.IncludeSocios ? ReceitaFileSet.Socios : 0);

        var digests = await source.EnsureLocalAsync(command.Release, fileSet, ct);

        if (!command.DryRun)
        {
            await using var uow = await unitOfWork.BeginAsync(ct);

            await releases.StartAsync(uow, ids.NewId(), command.Release, null, ct);
            await releases.RecordFilesAsync(uow, command.Release, digests, ct);
            await releases.RecordProgressAsync(
                uow, command.Release, ReceitaReleaseStatus.Downloaded, 0, 0, 0, 0, null, ct);

            await uow.CommitAsync(ct);
        }

        var tables = await source.ReadDomainTablesAsync(command.Release, ct);

        // ------------------------------------------------------------ passada A
        var scan = await ScanEstablishmentsAsync(command, tables, ct);

        logger.LogInformation(
            "Release {Release}: {Scanned} estabelecimento(s) lido(s), {Selected} no universo automotivo.",
            command.Release, scan.Scanned, scan.Selected);

        if (!command.DryRun)
        {
            await using var uow = await unitOfWork.BeginAsync(ct);

            await statistics.ReplaceAsync(
                uow, command.Release, scan.Stats.ByCnae, scan.Stats.ByMunicipio, tables.Municipios, ct);

            await uow.CommitAsync(ct);
        }

        if (command.StatsOnly || command.DryRun)
        {
            if (!command.DryRun)
            {
                await using var uow = await unitOfWork.BeginAsync(ct);
                await releases.FinishAsync(
                    uow, command.Release, ReceitaReleaseStatus.Streamed,
                    "stats-only: nenhuma empresa capturada", ct);
                await uow.CommitAsync(ct);
            }

            return new PrepareReceitaReleaseResult
            {
                Release = command.Release,
                EstablishmentsScanned = scan.Scanned,
                EstablishmentsSelected = scan.Selected,
                CompaniesJoined = 0,
                PartnersLoaded = 0,
                Files = digests,
                Statistics = scan.Stats.ByCnae,
                Rows = AsyncEmpty()
            };
        }

        // ------------------------------------------------------- passadas B e C
        var empresas = await ReadEmpresasAsync(command.Release, scan.SelectedRoots, ct);
        var simples = await ReadSimplesAsync(command.Release, scan.SelectedRoots, ct);

        logger.LogInformation(
            "Release {Release}: {Joined} de {Selected} raiz(es) casada(s) com o arquivo Empresas.",
            command.Release, empresas.Count, scan.SelectedRoots.Count);

        // --------------------------------------------------------- passada D
        var partnersLoaded = command.IncludeSocios
            ? await LoadPartnersAsync(command, scan.SelectedRoots, ct)
            : 0;

        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            await releases.RecordProgressAsync(
                uow, command.Release, ReceitaReleaseStatus.Streamed,
                scan.Scanned, scan.Selected, empresas.Count, partnersLoaded, null, ct);

            await uow.CommitAsync(ct);
        }

        return new PrepareReceitaReleaseResult
        {
            Release = command.Release,
            EstablishmentsScanned = scan.Scanned,
            EstablishmentsSelected = scan.Selected,
            CompaniesJoined = empresas.Count,
            PartnersLoaded = partnersLoaded,
            Files = digests,
            Statistics = [],
            Rows = JoinAsync(empresas, simples, tables, ct)
        };
    }

    /// <summary>
    /// Fecha o release depois que a captura e a resolucao do grafo terminaram.
    /// Chamado pelo orquestrador da CLI, que e quem sabe se as etapas seguintes
    /// deram certo.
    /// </summary>
    public async Task CompleteAsync(
        string release, Guid? batchId, string status, string? notes, CancellationToken ct = default)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);

        if (batchId is not null)
        {
            var summary = await releases.GetAsync(release, ct);

            await releases.RecordProgressAsync(
                uow, release, status,
                summary?.EstablishmentsScanned ?? 0,
                summary?.EstablishmentsSelected ?? 0,
                summary?.CompaniesJoined ?? 0,
                summary?.PartnersLoaded ?? 0,
                batchId, ct);
        }

        await releases.FinishAsync(uow, release, status, notes, ct);
        await uow.CommitAsync(ct);
    }

    // ----------------------------------------------------------------- passada A

    private sealed record ScanResult(
        long Scanned,
        long Selected,
        HashSet<int> SelectedRoots,
        MarketStatisticsAccumulator Stats);

    private async Task<ScanResult> ScanEstablishmentsAsync(
        PrepareReceitaReleaseCommand command, ReceitaDomainTables tables, CancellationToken ct)
    {
        var stats = new MarketStatisticsAccumulator();
        var roots = new HashSet<int>();

        if (!command.DryRun && !command.StatsOnly)
        {
            await spool.ResetAsync(MatrizSpool, ct);
            await spool.ResetAsync(FilialSpool, ct);
        }

        var matriz = new List<RawCompanyRow>(command.ChunkSize);
        var filial = new List<RawCompanyRow>(command.ChunkSize);

        long scanned = 0, selected = 0;

        await foreach (var est in source.ReadEstabelecimentosAsync(command.Release, ct))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            // O agregado ve TUDO. E o que impede o filtro abaixo de esconder o
            // que descartou: a revenda inativa nao entra em companies_raw, mas
            // continua contada aqui, por UF, CNAE e situacao.
            stats.Observe(est.Uf, est.CnaePrincipal, est.SituacaoCadastral, est.MatrizFilial, est.MunicipioCodigo);

            if (Selects(command, est))
            {
                selected++;

                if (!command.DryRun && !command.StatsOnly)
                {
                    // TryParse e nao Parse: uma raiz ilegivel numa linha e um
                    // registro a menos, nao o fim de uma carga de horas.
                    if (!int.TryParse(est.CnpjBasico, out var root)) continue;

                    roots.Add(root);

                    var row = ToRow(est, tables);
                    var target = est.MatrizFilial == "1" ? matriz : filial;
                    target.Add(row);

                    if (target.Count >= command.ChunkSize)
                    {
                        await spool.AppendAsync(
                            est.MatrizFilial == "1" ? MatrizSpool : FilialSpool, target, ct);
                        target.Clear();
                    }
                }
            }

            if (command.MaxEstablishments is { } max && scanned >= max) break;
        }

        if (matriz.Count > 0) await spool.AppendAsync(MatrizSpool, matriz, ct);
        if (filial.Count > 0) await spool.AppendAsync(FilialSpool, filial, ct);

        return new ScanResult(scanned, selected, roots, stats);
    }

    /// <summary>
    /// O filtro na origem. Deliberadamente restrito a tres testes que nao exigem
    /// julgamento: pertencer ao catalogo de CNAE, estar ativo e estar na UF
    /// pedida.
    ///
    /// Tudo o que exige interpretacao - digito verificador do CNPJ, UF que nao
    /// existe, nome ausente - continua acontecendo no <see cref="CompanyNormalizer"/>,
    /// DEPOIS que a linha ja esta em <c>companies_raw</c>, para que a rejeicao
    /// fique gravada com motivo e seja auditavel. Ver ADR-0007.
    /// </summary>
    private static bool Selects(PrepareReceitaReleaseCommand command, ReceitaEstabelecimento est)
    {
        if (command.ActiveOnly && !CompanyNormalizer.IsActiveRegistration(est.SituacaoCadastral))
        {
            return false;
        }

        if (command.Ufs is { Count: > 0 } ufs &&
            (est.Uf is null || !ufs.Contains(est.Uf.Trim().ToUpperInvariant())))
        {
            return false;
        }

        if (CnaeCatalog.IsInUniverse(est.CnaePrincipal)) return true;

        return command.IncludeSecondaryCnae
            && est.CnaesSecundarios is { Length: > 0 } secundarios
            && secundarios
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(CnaeCatalog.IsInUniverse);
    }

    /// <summary>
    /// Estabelecimento para linha crua. Preenche o que a passada A conhece e
    /// deixa vazio o que so vem de Empresas e de Simples.
    /// </summary>
    private static RawCompanyRow ToRow(ReceitaEstabelecimento est, ReceitaDomainTables tables) => new()
    {
        Cnpj = est.Cnpj,
        NomeFantasia = est.NomeFantasia,
        CnaePrincipal = est.CnaePrincipal,
        CnaesSecundarios = est.CnaesSecundarios,
        SituacaoCadastral = est.SituacaoCadastral,
        DataSituacaoCadastral = est.DataSituacaoCadastral,
        MotivoSituacaoCadastral = Describe(tables.Motivos, est.MotivoSituacaoCadastral),
        DataInicioAtividade = est.DataInicioAtividade,
        MatrizFilial = est.MatrizFilial,
        Uf = est.Uf,
        MunicipioCodigo = est.MunicipioCodigo,
        // Sem este join a coluna receberia "6219" no lugar de "Bauru", e tanto a
        // busca por cidade quanto a regra de mesma UF do account graph parariam
        // de funcionar.
        Municipio = Describe(tables.Municipios, est.MunicipioCodigo),
        Cep = est.Cep,
        Logradouro = Join(est.TipoLogradouro, est.Logradouro),
        Numero = est.Numero,
        Complemento = est.Complemento,
        Bairro = est.Bairro,
        Telefone1 = Join(est.Ddd1, est.Telefone1),
        Telefone2 = Join(est.Ddd2, est.Telefone2),
        Email = est.Email
    };

    // --------------------------------------------------------- passadas B, C e D

    private async Task<Dictionary<int, ReceitaEmpresa>> ReadEmpresasAsync(
        string release, HashSet<int> roots, CancellationToken ct)
    {
        var found = new Dictionary<int, ReceitaEmpresa>(roots.Count);

        await foreach (var empresa in source.ReadEmpresasAsync(release, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (!int.TryParse(empresa.CnpjBasico, out var root) || !roots.Contains(root)) continue;

            found[root] = empresa;
        }

        return found;
    }

    private async Task<Dictionary<int, ReceitaSimples>> ReadSimplesAsync(
        string release, HashSet<int> roots, CancellationToken ct)
    {
        var found = new Dictionary<int, ReceitaSimples>();

        await foreach (var simples in source.ReadSimplesAsync(release, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (!int.TryParse(simples.CnpjBasico, out var root) || !roots.Contains(root)) continue;

            found[root] = simples;
        }

        return found;
    }

    private async Task<long> LoadPartnersAsync(
        PrepareReceitaReleaseCommand command, HashSet<int> roots, CancellationToken ct)
    {
        var buffer = new List<CompanyPartnerRecord>(command.ChunkSize);
        long loaded = 0;

        await foreach (var socio in source.ReadSociosAsync(command.Release, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (!int.TryParse(socio.CnpjBasico, out var root) || !roots.Contains(root)) continue;

            buffer.Add(new CompanyPartnerRecord
            {
                CnpjBasico = socio.CnpjBasico,
                Identificador = socio.Identificador,
                Nome = socio.Nome,
                CpfCnpjMascarado = socio.CpfCnpj,
                Qualificacao = socio.Qualificacao,
                DataEntrada = ParseDate(socio.DataEntrada),
                Pais = socio.Pais,
                RepresentanteCpf = socio.RepresentanteCpf,
                RepresentanteNome = socio.RepresentanteNome,
                RepresentanteQualificacao = socio.RepresentanteQualificacao,
                FaixaEtaria = socio.FaixaEtaria
            });

            if (buffer.Count < command.ChunkSize) continue;

            loaded += await FlushPartnersAsync(command.Release, buffer, ct);
            buffer.Clear();
        }

        if (buffer.Count > 0)
        {
            loaded += await FlushPartnersAsync(command.Release, buffer, ct);
        }

        logger.LogInformation(
            "Release {Release}: {Loaded} socio(s) gravado(s) — PII sob a politica do frame 09.",
            command.Release, loaded);

        return loaded;
    }

    private async Task<int> FlushPartnersAsync(
        string release, IReadOnlyList<CompanyPartnerRecord> buffer, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);

        var written = await partners.UpsertAsync(uow, release, buffer, ct);
        await uow.CommitAsync(ct);

        return written;
    }

    // -------------------------------------------------------------------- juncao

    /// <summary>
    /// Le o spool de volta e completa cada linha com o que veio de Empresas e de
    /// Simples. Matrizes primeiro, pelo motivo documentado em
    /// <see cref="MatrizSpool"/>.
    /// </summary>
    private async IAsyncEnumerable<RawCompanyRow> JoinAsync(
        Dictionary<int, ReceitaEmpresa> empresas,
        Dictionary<int, ReceitaSimples> simples,
        ReceitaDomainTables tables,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var name in (string[])[MatrizSpool, FilialSpool])
        {
            await foreach (var row in spool.ReadAsync(name, ct))
            {
                ct.ThrowIfCancellationRequested();

                yield return Merge(row, empresas, simples, tables);
            }
        }
    }

    private static RawCompanyRow Merge(
        RawCompanyRow row,
        Dictionary<int, ReceitaEmpresa> empresas,
        Dictionary<int, ReceitaSimples> simples,
        ReceitaDomainTables tables)
    {
        if (row.Cnpj is not { Length: >= 8 } cnpj || !int.TryParse(cnpj[..8], out var root))
        {
            return row;
        }

        empresas.TryGetValue(root, out var empresa);
        simples.TryGetValue(root, out var opcao);

        return row with
        {
            // Razao social so existe no arquivo Empresas. Sem o par, sobra o nome
            // fantasia - e a linha sem nenhum dos dois sera rejeitada com
            // "missing_name" pelo normalizador, com o motivo gravado.
            RazaoSocial = empresa?.RazaoSocial,
            NaturezaJuridica = Describe(tables.Naturezas, empresa?.NaturezaJuridica),
            Porte = empresa?.Porte,
            CapitalSocial = empresa?.CapitalSocial,
            OpcaoSimples = opcao?.OpcaoSimples,
            OpcaoMei = opcao?.OpcaoMei
        };
    }

    // ------------------------------------------------------------------ apoio

    /// <summary>
    /// Codigo para descricao. Codigo sem par no dicionario volta como o proprio
    /// codigo, e nao como nulo: a Receita publica codigo novo antes de atualizar
    /// a tabela de dominio, e perder o dado seria pior que exibi-lo cru.
    /// </summary>
    private static string? Describe(IReadOnlyDictionary<string, string> table, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var key = code.Trim();

        return table.TryGetValue(key, out var description) ? description : key;
    }

    private static string? Join(string? prefix, string? value)
    {
        var left = prefix?.Trim();
        var right = value?.Trim();

        if (string.IsNullOrEmpty(right)) return null;

        return string.IsNullOrEmpty(left) ? right : $"{left} {right}";
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);

        return digits.Length == 8
            && DateOnly.TryParseExact(digits, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;
    }

#pragma warning disable CS1998 // sem await: a sequencia vazia nao tem o que aguardar
    private static async IAsyncEnumerable<RawCompanyRow> AsyncEmpty()
    {
        yield break;
    }
#pragma warning restore CS1998
}
