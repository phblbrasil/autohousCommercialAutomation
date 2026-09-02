using System.Text.Json;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Infrastructure;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AutoHous.Revenue.Ingestor;

/// <summary>
/// Calibração da severidade (ADR-0013): sonda uma amostra do mercado e grava a
/// distribuição real, para que o peso de cada achado saia do percentil do
/// segmento em vez de constante inventada.
///
/// **Custo de modelo: zero.** A sonda é HTTP puro.
///
/// **Sobre sair para a internet.** São quatro requisições por domínio — home,
/// robots.txt, sitemap.xml, llms.txt — uma vez, com concorrência limitada a 8 e
/// User-Agent que se identifica. A amostra é explícita e pequena por padrão:
/// 40 domínios por estrato, algumas centenas no total, nunca os 14 mil. Uma
/// ferramenta cujo propósito é medir se o site bloqueia robô mal-educado não
/// pode ser um.
/// </summary>
public static class CalibrarCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider provider, CalibrarOptions options, CancellationToken ct)
    {
        var connections = provider.GetRequiredService<NpgsqlConnectionFactory>();
        var probe = provider.GetRequiredService<IWebsiteProbe>();

        var candidatos = await SelecionarAsync(connections, options, ct);

        if (candidatos.Count == 0)
        {
            Log.Warning(
                "Nenhum candidato. Verifique se a carga da Receita rodou e se ha e-mail em dominio proprio.");
            return ExitCodes.Ok;
        }

        Log.Information(
            "Calibracao: {Total} dominio(s), concorrencia {Concorrencia}, sonda {Versao}.",
            candidatos.Count, options.Concurrency, probe.Name);

        var alcancados = 0;
        var falhas = 0;
        var processados = 0;

        // SemaphoreSlim e nao Parallel.ForEachAsync: o teto aqui e de EDUCACAO,
        // e nao de CPU. Parallel dimensiona pelo processador, que nao tem
        // relacao nenhuma com quantas conexoes simultaneas e razoavel abrir
        // contra sites de terceiros.
        using var limite = new SemaphoreSlim(options.Concurrency);

        var tarefas = candidatos.Select(async candidato =>
        {
            await limite.WaitAsync(ct);

            try
            {
                var resultado = await probe.ProbeAsync(CandidateDomain.ToUrl(candidato.Domain), ct);

                await GravarAsync(connections, candidato, resultado, probe.Name, ct);

                if (resultado.Reached) Interlocked.Increment(ref alcancados);
                else Interlocked.Increment(ref falhas);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Uma amostra perdida nao derruba a calibracao: o proposito e
                // distribuicao, e distribuicao tolera buraco. O que nao tolera e
                // parar no meio e ficar com estrato pela metade.
                Log.Debug(ex, "Falha ao sondar {Dominio}", candidato.Domain);
                Interlocked.Increment(ref falhas);
            }
            finally
            {
                limite.Release();

                var feitos = Interlocked.Increment(ref processados);

                if (feitos % 25 == 0)
                {
                    Log.Information("  {Feitos}/{Total} sondados", feitos, candidatos.Count);
                }
            }
        });

        await Task.WhenAll(tarefas);

        Log.Information(
            "Calibracao concluida: {Alcancados} alcancado(s), {Falhas} sem resposta.",
            alcancados, falhas);

        await ResumirAsync(connections, probe.Name, ct);

        return ExitCodes.Ok;
    }

    private sealed record Candidato(
        Guid AccountId, string Domain, string Natureza, string? Porte, int Unidades, string? Uf);

    /// <summary>
    /// Amostra ESTRATIFICADA por natureza e porte.
    ///
    /// Amostra simples pegaria quase só revenda micro — elas são 74% do ICP —, e
    /// a distribuição da concessionária média sairia de um punhado de linhas. O
    /// percentil por estrato exige tamanho POR estrato, e é isso que o
    /// <c>row_number()</c> particionado garante.
    /// </summary>
    private static async Task<List<Candidato>> SelecionarAsync(
        NpgsqlConnectionFactory connections, CalibrarOptions options, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<Candidato>(new CommandDefinition("""
            with base as (
              select c.account_id,
                     c.email,
                     c.porte,
                     c.uf,
                     case when c.cnae_principal in ('4511101','4511103','4511105')
                          then 'concessionaria' else 'revenda' end as natureza,
                     -- ::int porque `count` devolve bigint, e o record declara
                     -- int: sem o cast o Dapper nao materializa e a falha so
                     -- aparece em execucao.
                     count(*) over (partition by c.account_id)::int as unidades
                from companies_cnpj c
               where c.cnae_principal like '4511%'
                 and c.account_id is not null
                 and c.email is not null
                 and c.email like '%@%'
                 and c.situacao_cadastral = '02'
            ),
            ordenado as (
              select *,
                     row_number() over (
                       partition by natureza, porte
                       order by md5(account_id::text || @Seed)
                     ) as posicao
                from base
            )
            select account_id as AccountId,
                   email      as Domain,
                   natureza   as Natureza,
                   porte      as Porte,
                   unidades   as Unidades,
                   uf         as Uf
              from ordenado
             where posicao <= @PorEstrato
            """,
            new { options.Seed, PorEstrato = options.PerStratum },
            cancellationToken: ct));

        // A extracao do dominio acontece AQUI, e nao no SQL, porque a regra de
        // "isto e site de empresa?" vive no dominio (CandidateDomain) e reusa a
        // lista de provedores pessoais do ContactPolicy. Duplica-la em SQL
        // criaria uma segunda definicao que um dia diverge.
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidatos = new List<Candidato>();

        foreach (var row in rows)
        {
            if (CandidateDomain.FromEmail(row.Domain) is not { } domain) continue;
            if (!vistos.Add(domain)) continue;

            candidatos.Add(row with { Domain = domain });
        }

        return candidatos;
    }

    private static async Task GravarAsync(
        NpgsqlConnectionFactory connections,
        Candidato candidato,
        WebsiteProbeResult resultado,
        string versao,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition("""
            insert into probe_samples
                (account_id, domain, domain_source, natureza, porte, unidades, uf,
                 probe_version, reached, status_code, probe)
            values
                (@AccountId, @Domain, 'receita_email', @Natureza, @Porte, @Unidades,
                 cast(@Uf as char(2)), @Versao, @Reached, @StatusCode, @Probe::jsonb)
            on conflict (domain, probe_version) do update
                set sampled_at  = now(),
                    reached     = excluded.reached,
                    status_code = excluded.status_code,
                    probe       = excluded.probe
            """,
            new
            {
                candidato.AccountId,
                candidato.Domain,
                candidato.Natureza,
                candidato.Porte,
                candidato.Unidades,
                candidato.Uf,
                Versao = versao,
                resultado.Reached,
                resultado.StatusCode,
                Probe = JsonSerializer.Serialize(Achatar(resultado))
            }, cancellationToken: ct));
    }

    /// <summary>
    /// Achata o resultado para o jsonb que a view consulta.
    ///
    /// Duração vira milissegundo numérico de propósito: <c>TimeSpan</c>
    /// serializa como <c>"00:00:00.21"</c>, e `percentile_cont` sobre string
    /// ordena alfabeticamente — "00:00:10" viria antes de "00:00:9". O erro
    /// passaria despercebido porque o número sai plausível.
    /// </summary>
    private static Dictionary<string, object?> Achatar(WebsiteProbeResult r) => new()
    {
        ["timeToFirstByteMs"] = r.TimeToFirstByte?.TotalMilliseconds,
        ["documentLoadMs"] = r.DocumentLoadTime?.TotalMilliseconds,
        ["documentBytes"] = r.DocumentBytes,
        ["renderBlockingResources"] = r.RenderBlockingResources,
        ["compressionEnabled"] = r.CompressionEnabled,

        ["isHttps"] = r.IsHttps,
        ["hasTitle"] = r.HasTitle,
        ["hasMetaDescription"] = r.HasMetaDescription,
        ["hasH1"] = r.HasH1,
        ["hasCanonical"] = r.HasCanonical,
        ["hasStructuredData"] = r.HasStructuredData,
        ["hasSitemap"] = r.HasSitemap,
        ["hasRobotsTxt"] = r.HasRobotsTxt,
        ["hasViewportMeta"] = r.HasViewportMeta,
        ["hasFixedWidthViewport"] = r.HasFixedWidthViewport,

        ["aiCrawlersBlocked"] = r.AiCrawlersBlocked,
        ["aiSearchCrawlersBlocked"] = r.AiCrawlersBlocked is null
            ? null
            : AiCrawlers.CountSearch(r.AiCrawlersBlocked),
        ["hasLlmsTxt"] = r.HasLlmsTxt,
        ["isIndexable"] = r.IsIndexable,
        ["rawTextWords"] = r.RawTextWords,

        ["structuredDataTypes"] = r.StructuredDataTypes,
        ["structuredDataHasNap"] = r.StructuredDataHasNap,
        ["h1Count"] = r.H1Count,
        ["h2Count"] = r.H2Count,

        ["titleLength"] = r.TitleLength,
        ["metaDescriptionLength"] = r.MetaDescriptionLength,
        ["canonicalIsSelfReferencing"] = r.CanonicalIsSelfReferencing,
        ["imageCount"] = r.ImageCount,
        ["imagesWithAlt"] = r.ImagesWithAlt,
        ["imagesWithDimensions"] = r.ImagesWithDimensions,
        ["imagesModernFormat"] = r.ImagesModernFormat,
        ["hasHsts"] = r.HasHsts,
        ["internalLinkCount"] = r.InternalLinkCount,
        ["declaredLanguage"] = r.DeclaredLanguage,

        ["technologies"] = r.Technologies.Select(t => new { t.Category, t.Name })
    };

    private static async Task ResumirAsync(
        NpgsqlConnectionFactory connections, string versao, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var linhas = await connection.QueryAsync(new CommandDefinition("""
            select natureza, porte, n, n_alcancados, n_com_robots,
                   pct_bloqueia_busca_ia, pct_com_vehicle, pct_com_nap,
                   mediana_ttfb_ms, mediana_palavras
              from v_probe_distribution
             where probe_version = @Versao
             order by natureza, porte
            """, new { Versao = versao }, cancellationToken: ct));

        Log.Information("Distribuicao por estrato:");

        foreach (var l in linhas)
        {
            Log.Information(
                "  {Natureza}/{Porte} n={N} alcancados={Alc} | bloqueia busca IA {Ia:P0} " +
                "(de {ComRobots} com robots) | Vehicle {Veh:P0} | NAP {Nap:P0} | " +
                "TTFB mediano {Ttfb:0}ms | palavras {Pal:0}",
                l.natureza, l.porte, l.n, l.n_alcancados,
                l.pct_bloqueia_busca_ia, l.n_com_robots,
                l.pct_com_vehicle, l.pct_com_nap,
                l.mediana_ttfb_ms, l.mediana_palavras);
        }

        Log.Information(
            "Estrato com n baixo nao sustenta percentil - ver ADR-0013 antes de usar o numero.");
    }
}

public sealed record CalibrarOptions
{
    /// <summary>Domínios por estrato (natureza × porte). Padrão conservador de propósito.</summary>
    public int PerStratum { get; init; } = 40;

    /// <summary>Requisições simultâneas contra sites de terceiros.</summary>
    public int Concurrency { get; init; } = 8;

    /// <summary>
    /// Semente da amostra. Fixa por padrão para que reexecutar sonde os MESMOS
    /// domínios: comparar duas safras exige a mesma amostra, senão a diferença
    /// medida pode ser só troca de quem foi sorteado.
    /// </summary>
    public string Seed { get; init; } = "autohous-2026";

    public static CalibrarOptions? Parse(string[] args)
    {
        var options = new CalibrarOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--por-estrato" when i + 1 < args.Length && int.TryParse(args[++i], out var n):
                    options = options with { PerStratum = Math.Clamp(n, 1, 2000) };
                    break;

                case "--concorrencia" when i + 1 < args.Length && int.TryParse(args[++i], out var c):
                    options = options with { Concurrency = Math.Clamp(c, 1, 16) };
                    break;

                case "--semente" when i + 1 < args.Length:
                    options = options with { Seed = args[++i] };
                    break;

                default:
                    return null;
            }
        }

        return options;
    }
}
