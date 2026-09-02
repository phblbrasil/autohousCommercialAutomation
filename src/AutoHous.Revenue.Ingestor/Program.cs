using System.Text;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Infrastructure;
using AutoHous.Revenue.Ingestor;
using AutoHous.Revenue.ReceitaFederal;
using AutoHous.Revenue.WebAudit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

// ---------------------------------------------------------------------------
// CLI de captura. Dois subcomandos, um pipeline:
//
//   receita   base oficial de Dados Abertos CNPJ -> companies_raw -> account graph
//   arquivo   extrato delimitado de terceiro     -> companies_raw -> account graph
//
// Separada da API de proposito. Um release da Receita tem 7 GB e dezenas de
// milhoes de linhas; enviar isso por HTTP significaria upload multipart, timeout
// de gateway e um endpoint que segura memoria. O endpoint POST /ingestion/batches
// continua existindo para listas pequenas e para teste.
// ---------------------------------------------------------------------------

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var (verb, rest) = Dispatch(args);

if (verb is null)
{
    PrintUsage();
    return ExitCodes.BadArguments;
}

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

var connectionString =
    Environment.GetEnvironmentVariable("REVENUE_DB_CONNECTION")
    ?? throw new InvalidOperationException("REVENUE_DB_CONNECTION nao configurada. Ver .env.example.");

var services = new ServiceCollection();
services.AddLogging(b => b.AddSerilog(dispose: true));
services.AddRevenueInfrastructure(connectionString);
services.AddRevenueUseCases();

ReceitaCommandOptions? receitaOptions = null;
IngestorOptions? fileOptions = null;
CalibrarOptions? calibrarOptions = null;

switch (verb)
{
    case "receita":
        receitaOptions = ReceitaCommandOptions.Parse(rest);

        if (receitaOptions is null)
        {
            PrintUsage();
            return ExitCodes.BadArguments;
        }

        services.AddReceitaFederalSource(o =>
        {
            if (receitaOptions.CacheDir is { Length: > 0 } cache) o.CacheDirectory = cache;
            if (receitaOptions.WorkDir is { Length: > 0 } work) o.WorkDirectory = work;
            o.OfflineOnly = receitaOptions.Offline;
        });
        break;

    case "calibrar":
        calibrarOptions = CalibrarOptions.Parse(rest);

        if (calibrarOptions is null)
        {
            PrintUsage();
            return ExitCodes.BadArguments;
        }

        // A sonda de site so entra no container do Ingestor para este
        // subcomando: os outros nao saem para a internet, e registrar um
        // HttpClient que eles nunca usam so aumentaria a superficie.
        services.AddHttpWebsiteProbe();
        break;

    case "arquivo":
        fileOptions = IngestorOptions.Parse(rest);

        if (fileOptions is null)
        {
            PrintUsage();
            return ExitCodes.BadArguments;
        }
        break;
}

await using var provider = services.BuildServiceProvider();

using var cancellation = new CancellationTokenSource();

// Ctrl+C durante uma carga de horas deve encerrar o bloco corrente e sair
// limpo, e nao abandonar transacao aberta.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    return verb switch
    {
        "receita" => await ReceitaCommand.RunAsync(provider, receitaOptions!, cancellation.Token),
        "calibrar" => await CalibrarCommand.RunAsync(provider, calibrarOptions!, cancellation.Token),
        _ => await FileIngestCommand.RunAsync(provider, fileOptions!, cancellation.Token)
    };
}
catch (OperationCanceledException)
{
    Log.Warning("Interrompido. O que ja foi gravado esta consistente; reexecute para continuar.");
    return ExitCodes.SourceFailure;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Escolhe o subcomando. Sem verbo reconhecido, cai no modo arquivo — o
/// <c>--file</c> que ja estava documentado continua funcionando sem mudanca.
/// </summary>
static (string? Verb, string[] Arguments) Dispatch(string[] args)
{
    if (args.Length == 0) return (null, []);

    return args[0] switch
    {
        "receita" => ("receita", args[1..]),
        "calibrar" => ("calibrar", args[1..]),
        "arquivo" => ("arquivo", args[1..]),
        _ => ("arquivo", args)
    };
}

static void PrintUsage() => Console.Error.WriteLine(
    """
    Uso: AutoHous.Revenue.Ingestor <subcomando> [opcoes]

    receita — base oficial de Dados Abertos CNPJ da Receita Federal

      -l, --list                       lista as competencias publicadas e sai
      -r, --release   <AAAA-MM>        competencia. Padrao: a mais recente
          --uf        <SP,PR>          recorte por UF. Padrao: pais inteiro
          --stats-only                 so o agregado de mercado, sem capturar empresa
          --dry-run                    le e conta, nao grava nada
          --limit     <n>              para depois de n estabelecimentos (so com --dry-run)
          --socios                     carrega o quadro societario — PII, ver docs/governance.md
          --incluir-inativos           mantem situacao cadastral nao ativa
          --incluir-cnae-secundario    admite CNAE do catalogo entre os secundarios
          --cache-dir <caminho>        onde os zips ficam. Padrao: .receita-cache
          --work-dir  <caminho>        onde o spool fica.  Padrao: .receita-work
          --keep-spool                 nao apaga o spool ao final
          --offline                    usa so o que ja esta no cache; exige --release
          --resolve-batch <uuid>       retoma a resolucao de um lote ja capturado;
                                       pula download, leitura e captura

    arquivo — extrato delimitado (modo padrao quando o subcomando e omitido)

      -f, --file       <caminho>   arquivo delimitado com as empresas (obrigatorio)
      -d, --delimiter  <char>      delimitador; 'tab' aceito. Padrao: ;
      -e, --encoding   <nome>      encoding do arquivo. Padrao: utf-8 (Receita: latin1)
      -s, --source     <nome>      rotulo da fonte gravado no lote. Padrao: nome do arquivo
          --dry-run                simula a normalizacao e nao grava nada

      Colunas reconhecidas (por apelido, sem depender da ordem):
        cnpj, razao_social, nome_fantasia, cnae_principal,
        situacao_cadastral, municipio, uf

    Exige REVENUE_DB_CONNECTION no ambiente.

    Codigos de saida:
      0  lote capturado e grafo resolvido
      1  sem linhas de dados
      2  argumentos invalidos
      3  gravado, mas abaixo do quality gate de 85% de resolucao automatica
      4  fonte da Receita indisponivel, incompleta ou com layout inesperado
    """);
