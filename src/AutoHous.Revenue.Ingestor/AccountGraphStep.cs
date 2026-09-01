using AutoHous.Revenue.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Ingestor;

/// <summary>
/// Resolucao do account graph e impressao do resumo.
///
/// Compartilhada pelos dois subcomandos de proposito: um extrato de terceiro e a
/// base oficial da Receita divergem na LEITURA e em nada depois dela. Duplicar
/// esta etapa faria a fonte nova poder agrupar contas por uma regra diferente da
/// fonte antiga - e o account graph e o principio de desenho no 1 da V2.
/// </summary>
internal static class AccountGraphStep
{
    /// <summary>O frame 04 pede >= 85% de resolucao sem intervencao humana.</summary>
    private const decimal Gate = 0.85m;

    internal sealed record Outcome(ResolveAccountGraphResult Graph, int ExitCode);

    public static Task<Outcome> RunAsync(
        IServiceProvider provider,
        IngestCompanyBatchResult captured,
        ILogger logger,
        CancellationToken ct) =>
        RunAsync(provider, captured.BatchId, captured, logger, ct);

    /// <summary>
    /// Retomada: resolve um lote ja capturado, sem os numeros da captura.
    ///
    /// O resumo omite "linhas lidas / gravadas / duplicadas" de proposito - esses
    /// numeros pertencem a execucao que capturou o lote, e reimprimi-los aqui
    /// sugeriria que esta execucao os produziu.
    /// </summary>
    public static Task<Outcome> ResumeAsync(
        IServiceProvider provider,
        Guid batchId,
        ILogger logger,
        CancellationToken ct) =>
        RunAsync(provider, batchId, captured: null, logger, ct);

    private static async Task<Outcome> RunAsync(
        IServiceProvider provider,
        Guid batchId,
        IngestCompanyBatchResult? captured,
        ILogger logger,
        CancellationToken ct)
    {
        var resolve = provider.GetRequiredService<ResolveAccountGraphUseCase>();
        var graph = await resolve.ExecuteAsync(batchId, ct);

        Console.WriteLine();
        Console.WriteLine($"Lote {batchId}");

        if (captured is not null)
        {
            Console.WriteLine($"  linhas lidas ............ {captured.TotalRows}");
            Console.WriteLine($"  gravadas ................ {captured.AcceptedRows}");
            Console.WriteLine($"  duplicadas .............. {captured.DuplicateRows}");
        }
        else
        {
            Console.WriteLine($"  linhas resolvidas ....... {graph.Processed} (retomada)");
        }

        Console.WriteLine($"  rejeitadas .............. {graph.Rejected}");
        Console.WriteLine($"  contas criadas .......... {graph.CreatedAccounts}");
        Console.WriteLine($"  CNPJs anexados .......... {graph.AttachedCnpjs}");
        Console.WriteLine($"  em revisao humana ....... {graph.ReviewCandidates}");
        Console.WriteLine($"  resolucao automatica .... {graph.AutoResolvedRate:P1}");

        // Falhar o gate nao invalida o lote — os dados estao la —, mas precisa
        // ser visivel no exit code para que um pipeline de carga nao siga adiante
        // sem alguem olhar.
        if (graph.Processed > 0 && graph.AutoResolvedRate < Gate)
        {
            logger.LogWarning(
                "Quality gate de agrupamento nao atingido: {Actual:P1} < {Gate:P0}. " +
                "{Review} linha(s) aguardam revisao em /merge-candidates.",
                graph.AutoResolvedRate, Gate, graph.ReviewCandidates);

            return new Outcome(graph, ExitCodes.QualityGate);
        }

        return new Outcome(graph, ExitCodes.Ok);
    }
}
