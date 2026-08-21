using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AutoHous.Revenue.Application;
using Microsoft.Extensions.Options;

namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>
/// Spool em disco, uma linha JSON por registro.
///
/// NDJSON e nao um formato binario proprio: o spool e o unico ponto do pipeline
/// onde da para olhar o que a passada A selecionou antes de qualquer coisa ir
/// para o banco. <c>head -1</c> resolvendo uma duvida de mapeamento vale mais que
/// os bytes economizados.
/// </summary>
public sealed class FileReceitaSpool(IOptions<ReceitaOptions> options) : IReceitaSpool
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ReceitaOptions _options = options.Value;

    public Task ResetAsync(string name, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.WorkDirectory);

        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);

        return Task.CompletedTask;
    }

    public async Task AppendAsync(
        string name, IReadOnlyList<RawCompanyRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        Directory.CreateDirectory(_options.WorkDirectory);

        var builder = new StringBuilder(rows.Count * 512);

        foreach (var row in rows)
        {
            builder.AppendLine(JsonSerializer.Serialize(row, Json));
        }

        await File.AppendAllTextAsync(PathFor(name), builder.ToString(), ct);
    }

    public async IAsyncEnumerable<RawCompanyRow> ReadAsync(
        string name, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = PathFor(name);

        // Spool vazio e estado legitimo: um recorte por UF pode nao ter nenhuma
        // matriz, e isso nao e erro.
        if (!File.Exists(path)) yield break;

        using var reader = new StreamReader(path);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;

            var row = JsonSerializer.Deserialize<RawCompanyRow>(line, Json);

            if (row is not null) yield return row;
        }
    }

    public Task DeleteAsync(string name, CancellationToken ct = default) => ResetAsync(name, ct);

    private string PathFor(string name) =>
        Path.Combine(_options.WorkDirectory, $"{name}.ndjson");
}
