using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Ingestor;

/// <summary>
/// Subcomando <c>receita</c>: carga da base oficial de Dados Abertos CNPJ.
///
/// Orquestra as tres etapas que ja existiam separadas, na unica ordem que
/// funciona:
///
///   1. <see cref="PrepareReceitaReleaseUseCase"/>  fonte -> agregado + linhas
///   2. <see cref="IngestCompanyStreamUseCase"/>    linhas -> companies_raw
///   3. <see cref="ResolveAccountGraphUseCase"/>    companies_raw -> account graph
///
/// A composicao mora aqui, e nao dentro de um caso de uso, pelo mesmo motivo que
/// ja valia no modo arquivo: a CLI e o unico lugar que pode conhecer os tres.
/// </summary>
internal static class ReceitaCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider provider, ReceitaCommandOptions options, CancellationToken ct)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Receita");

        // Offline: nao ha origem para consultar, e o release ja veio obrigatorio
        // do parser. Listar competencias nesse modo seria pedir rede justamente
        // para quem disse que nao tem.
        if (options.Offline)
        {
            return await LoadAsync(provider, options, options.Release!, logger, ct);
        }

        var archive = provider.GetRequiredService<IReceitaFederalArchive>();

        IReadOnlyList<string> releases;

        try
        {
            releases = await archive.ListReleasesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogError(ex, "Nao foi possivel falar com o repositorio da Receita Federal.");
            return ExitCodes.SourceFailure;
        }

        if (releases.Count == 0)
        {
            logger.LogError("O repositorio da Receita nao listou nenhuma competencia.");
            return ExitCodes.SourceFailure;
        }

        if (options.List)
        {
            Console.WriteLine();
            Console.WriteLine($"{releases.Count} competencia(s) publicada(s):");
            Console.WriteLine();

            foreach (var line in releases.Chunk(6))
            {
                Console.WriteLine("  " + string.Join("  ", line));
            }

            Console.WriteLine();
            Console.WriteLine($"  mais recente: {releases[^1]}");

            return ExitCodes.Ok;
        }

        var release = options.Release ?? releases[^1];

        if (!releases.Contains(release))
        {
            logger.LogError(
                "Competencia '{Release}' nao publicada. Disponiveis: {First} a {Last}.",
                release, releases[0], releases[^1]);

            return ExitCodes.BadArguments;
        }

        return await LoadAsync(provider, options, release, logger, ct);
    }

    private static async Task<int> LoadAsync(
        IServiceProvider provider,
        ReceitaCommandOptions options,
        string release,
        ILogger logger,
        CancellationToken ct)
    {
        var prepare = provider.GetRequiredService<PrepareReceitaReleaseUseCase>();

        var command = new PrepareReceitaReleaseCommand
        {
            Release = release,
            ActiveOnly = !options.IncludeInactive,
            IncludeSecondaryCnae = options.IncludeSecondaryCnae,
            Ufs = options.Ufs,
            IncludeSocios = options.IncludeSocios,
            StatsOnly = options.StatsOnly,
            DryRun = options.DryRun,
            MaxEstablishments = options.Limit
        };

        PrepareReceitaReleaseResult prepared;

        try
        {
            prepared = await prepare.ExecuteAsync(command, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException
                                         or FileNotFoundException or DirectoryNotFoundException
                                         or InvalidOperationException)
        {
            logger.LogError(ex, "Falha na fonte da Receita para o release {Release}.", release);
            await SafeFailAsync(prepare, release, ex.Message, options, ct);
            return ExitCodes.SourceFailure;
        }

        PrintSource(prepared, options);

        if (options.DryRun || options.StatsOnly)
        {
            PrintStatistics(prepared);
            return prepared.EstablishmentsScanned == 0 ? ExitCodes.NoData : ExitCodes.Ok;
        }

        if (prepared.EstablishmentsSelected == 0)
        {
            logger.LogError(
                "Nenhum estabelecimento do universo automotivo em {Release} com este recorte.", release);

            await prepare.CompleteAsync(
                release, null, ReceitaReleaseStatus.Loaded, "nenhuma linha selecionada", ct);

            return ExitCodes.NoData;
        }

        var ingest = provider.GetRequiredService<IngestCompanyStreamUseCase>();

        var captured = await ingest.ExecuteAsync(new IngestCompanyStreamCommand
        {
            SourceName = $"receita-{release}",
            SourceUri = $"receita-federal://dados-abertos-cnpj/{release}",
            Rows = prepared.Rows
        }, ct);

        var outcome = await AccountGraphStep.RunAsync(provider, captured, logger, ct);

        await prepare.CompleteAsync(
            release,
            captured.BatchId,
            ReceitaReleaseStatus.Loaded,
            outcome.ExitCode == ExitCodes.QualityGate
                ? $"quality gate nao atingido: {outcome.Graph.AutoResolvedRate:P1}"
                : null,
            ct);

        if (!options.KeepSpool)
        {
            var spool = provider.GetRequiredService<IReceitaSpool>();
            await spool.DeleteAsync("matriz", ct);
            await spool.DeleteAsync("filial", ct);
        }

        return outcome.ExitCode;
    }

    private static void PrintSource(PrepareReceitaReleaseResult prepared, ReceitaCommandOptions options)
    {
        var bytes = prepared.Files.Sum(f => f.Length);

        Console.WriteLine();
        Console.WriteLine($"Release {prepared.Release}{(options.DryRun ? "  (ensaio — nada gravado)" : string.Empty)}");
        Console.WriteLine($"  arquivos ................ {prepared.Files.Count} ({bytes / 1024d / 1024d:N0} MB)");
        Console.WriteLine($"  estabelecimentos lidos .. {prepared.EstablishmentsScanned:N0}");
        Console.WriteLine($"  universo automotivo ..... {prepared.EstablishmentsSelected:N0}");

        if (prepared.CompaniesJoined > 0)
        {
            Console.WriteLine($"  empresas casadas ........ {prepared.CompaniesJoined:N0}");
        }

        if (prepared.PartnersLoaded > 0)
        {
            Console.WriteLine($"  socios gravados ......... {prepared.PartnersLoaded:N0}  (PII)");
        }
    }

    /// <summary>
    /// O recorte que importa para quem vai prospectar: quantos estabelecimentos
    /// ativos do ICP central existem, por UF.
    /// </summary>
    private static void PrintStatistics(PrepareReceitaReleaseResult prepared)
    {
        var core = prepared.Statistics
            .Where(s => CompanyNormalizer.IsActiveRegistration(s.SituacaoCadastral))
            .Where(s => CnaeCatalog.Classify(s.Cnae) is { InCoreIcp: true })
            .GroupBy(s => s.Uf)
            .Select(g => (Uf: g.Key.Length == 0 ? "--" : g.Key, Total: g.Sum(s => s.Establishments)))
            .OrderByDescending(g => g.Total)
            .ToList();

        if (core.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"ICP central ativo — {core.Sum(c => c.Total):N0} estabelecimento(s):");
        Console.WriteLine();

        foreach (var (uf, total) in core.Take(10))
        {
            Console.WriteLine($"  {uf}  {total,10:N0}");
        }

        if (core.Count > 10)
        {
            Console.WriteLine($"  ... mais {core.Count - 10} UF(s)");
        }
    }

    private static async Task SafeFailAsync(
        PrepareReceitaReleaseUseCase prepare,
        string release,
        string notes,
        ReceitaCommandOptions options,
        CancellationToken ct)
    {
        if (options.DryRun) return;

        try
        {
            await prepare.CompleteAsync(release, null, ReceitaReleaseStatus.Failed, notes, ct);
        }
        catch (Exception)
        {
            // Registrar a falha e melhor esforco: se o banco tambem caiu, o log
            // ja carrega o motivo e insistir aqui esconderia a excecao original.
        }
    }
}
