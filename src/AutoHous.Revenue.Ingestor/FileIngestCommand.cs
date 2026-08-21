using System.Text;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoHous.Revenue.Ingestor;

/// <summary>
/// Subcomando <c>arquivo</c>: captura de um extrato delimitado.
///
/// Continua sendo o caminho para lista pequena e para fonte de terceiro. A base
/// oficial da Receita entra pelo subcomando <c>receita</c>, que nao depende de
/// ninguem ter achatado o dado antes.
/// </summary>
internal static class FileIngestCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider provider, IngestorOptions options, CancellationToken ct)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Ingestor");

        var reader = new DelimitedCompanyReader();
        var encoding = Encoding.GetEncoding(options.Encoding);

        logger.LogInformation(
            "Lendo {Path} (delimitador '{Delimiter}', encoding {Encoding})",
            options.Path, options.Delimiter, options.Encoding);

        var read = await reader.ReadAsync(options.Path, options.Delimiter, encoding, ct);

        if (read.UnmappedColumns.Count > 0)
        {
            // Aviso e nao erro: colunas extras sao normais. Mas se a coluna que
            // faltou for a de CNAE, o lote inteiro cai em "unknown_cnae" e o
            // silencio aqui custaria uma investigacao.
            logger.LogWarning(
                "Colunas nao mapeadas (ignoradas): {Columns}", string.Join(", ", read.UnmappedColumns));
        }

        if (read.Rows.Count == 0)
        {
            logger.LogError("Nenhuma linha de dados em {Path}.", options.Path);
            return ExitCodes.NoData;
        }

        // Pre-visualizacao: quantas linhas do arquivo sobrevivem a normalizacao,
        // sem escrever nada. Roda o MESMO codigo de dominio do pipeline real,
        // entao o numero nao e estimativa.
        if (options.DryRun)
        {
            var outcomes = read.Rows
                .Select(r => CompanyNormalizer.Normalize(r.ToFields()))
                .GroupBy(r => r.ReasonLabel)
                .OrderByDescending(g => g.Count())
                .ToList();

            Console.WriteLine();
            Console.WriteLine($"Simulacao de {read.Rows.Count} linha(s) — nada foi gravado:");

            foreach (var group in outcomes)
            {
                Console.WriteLine($"  {group.Key,-24} {group.Count(),8}");
            }

            var accepted = outcomes.FirstOrDefault(g => g.Key == "accepted")?.Count() ?? 0;
            Console.WriteLine();
            Console.WriteLine($"  aproveitamento: {(decimal)accepted / read.Rows.Count:P1}");

            return ExitCodes.Ok;
        }

        var ingest = provider.GetRequiredService<IngestCompanyBatchUseCase>();

        var captured = await ingest.ExecuteAsync(new IngestCompanyBatchCommand
        {
            SourceName = options.SourceName ?? Path.GetFileName(options.Path),
            SourceUri = Path.GetFullPath(options.Path),
            Rows = read.Rows
        }, ct);

        var graph = await AccountGraphStep.RunAsync(provider, captured, logger, ct);

        return graph.ExitCode;
    }
}
