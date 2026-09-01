using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AutoHous.Revenue.Agents;

/// <summary>
/// Valida a saida de um agente contra um JSON Schema e so entao desserializa.
///
/// Este e o ponto que o ADR-002 exige na pratica: o LLM sugere, a plataforma
/// valida. Nenhum caminho de codigo deve desserializar saida de agente sem
/// passar por aqui.
/// </summary>
public sealed class StructuredOutputValidator : IStructuredOutputValidator
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        // Sem isto, "format": "uri" e "date-time" seriam apenas anotacoes e um
        // observed_at absurdo passaria batido.
        RequireFormatValidation = true
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// JsonSchema.FromText registra o schema pelo seu $id em um registro GLOBAL e
    /// lanca "Overwriting registered schemas is not permitted" na segunda
    /// compilacao do mesmo $id. Cachear por conteudo evita que instanciar o
    /// validador duas vezes - em um retry, em outro escopo de DI, em outro teste -
    /// derrube o processo.
    ///
    /// Lazy e nao apenas ConcurrentDictionary: GetOrAdd pode executar a fabrica
    /// mais de uma vez sob concorrencia, e duas compilacoes simultaneas do mesmo
    /// $id colidem no registro global. LazyThreadSafetyMode.ExecutionAndPublication
    /// garante compilacao unica.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> SchemaCache = new();

    /// <summary>
    /// Schema POR TIPO DE CONTRATO, e nao um schema so.
    ///
    /// Ate o Website Auditor existir, o validador guardava um unico schema e
    /// <c>Validate&lt;T&gt;</c> o usava fosse qual fosse T - o que era correto
    /// enquanto havia um agente. Com dois, a saida do auditor seria validada
    /// contra o schema do Research Profile e reprovaria inteira, com violacoes
    /// falando de campos que o auditor nunca deveria ter.
    ///
    /// A alternativa era registrar dois validadores com chave no DI e fazer cada
    /// caso de uso pedir o seu. Isto e melhor: <c>Validate&lt;T&gt;</c> ja diz
    /// qual e o contrato, entao o proprio T e a chave. A porta nao muda, o caso
    /// de uso nao ganha atributo de DI, e nao existe o erro de pedir o validador
    /// errado - ele deixou de ser expressavel.
    /// </summary>
    private readonly IReadOnlyDictionary<Type, JsonSchema> _schemas;

    public StructuredOutputValidator(string schemaJson)
        : this(new Dictionary<Type, string> { [typeof(object)] = schemaJson }) { }

    public StructuredOutputValidator(IReadOnlyDictionary<Type, string> schemasByContract)
    {
        _schemas = schemasByContract.ToDictionary(
            pair => pair.Key,
            pair => SchemaCache.GetOrAdd(
                pair.Value,
                static json => new Lazy<JsonSchema>(
                    () => JsonSchema.FromText(json),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value);
    }

    public static StructuredOutputValidator FromFile(string path) =>
        new(File.ReadAllText(path));

    /// <summary>Um schema por contrato, lidos de disco.</summary>
    public static StructuredOutputValidator FromFiles(IReadOnlyDictionary<Type, string> pathsByContract) =>
        new(pathsByContract.ToDictionary(p => p.Key, p => File.ReadAllText(p.Value)));

    /// <summary>
    /// Extrai, valida e desserializa. Falha em qualquer etapa retorna as
    /// violacoes - nunca um objeto parcialmente preenchido.
    /// </summary>
    public ValidationOutcome<T> Validate<T>(string? rawText)
    {
        if (!TryResolveSchema<T>(out var schema))
        {
            // Erro NOSSO, de composicao, e nao do agente: alguem registrou um
            // caso de uso sem registrar o schema do contrato dele. Falha como
            // violacao em vez de excecao para que o motivo chegue ao
            // research_runs.error junto das demais, em vez de virar stack trace.
            return ValidationOutcome<T>.Fail(new SchemaViolation(
                string.Empty,
                $"Nenhum schema registrado para o contrato {typeof(T).Name}. " +
                "Ver AddAgentValidators na composicao do host."));
        }

        if (!JsonPayloadExtractor.TryExtract(rawText, out var node, out var extractionError))
        {
            return ValidationOutcome<T>.Fail(new SchemaViolation(string.Empty, extractionError!));
        }

        // JsonSchema.Net 9.x avalia sobre JsonElement; o extractor trabalha com
        // JsonNode porque precisa tolerar payload sujo antes de qualquer parse.
        var element = System.Text.Json.JsonSerializer.SerializeToElement(node);
        var results = schema.Evaluate(element, Options);

        if (!results.IsValid)
        {
            return ValidationOutcome<T>.Fail(Flatten(results));
        }

        try
        {
            var value = node.Deserialize<T>(SerializerOptions);

            return value is null
                ? ValidationOutcome<T>.Fail(new SchemaViolation(string.Empty, "Payload desserializou para nulo."))
                : ValidationOutcome<T>.Ok(value);
        }
        catch (JsonException ex)
        {
            // Schema valido mas desserializacao falhou significa divergencia
            // entre o schema e os records de contrato - erro nosso, nao do agente.
            return ValidationOutcome<T>.Fail(
                new SchemaViolation(string.Empty, $"Falha ao desserializar apos schema valido: {ex.Message}"));
        }
    }

    /// <summary>
    /// Resolve o schema pelo contrato. O <c>typeof(object)</c> e a compatibilidade
    /// do construtor de schema unico: com ele, o validador se comporta como antes
    /// e valida qualquer T contra o unico schema que tem - o que mantem os testes
    /// que constroem o validador com um schema so, e a semantica que eles fixam.
    /// </summary>
    private bool TryResolveSchema<T>(out JsonSchema schema)
    {
        if (_schemas.TryGetValue(typeof(T), out var exact))
        {
            schema = exact;
            return true;
        }

        if (_schemas.TryGetValue(typeof(object), out var fallback))
        {
            schema = fallback;
            return true;
        }

        schema = null!;
        return false;
    }

    private static List<SchemaViolation> Flatten(EvaluationResults results)
    {
        var violations = new List<SchemaViolation>();
        Collect(results, violations);

        if (violations.Count == 0)
        {
            violations.Add(new SchemaViolation(string.Empty, "Payload nao satisfaz o schema."));
        }

        return violations;
    }

    private static void Collect(EvaluationResults node, List<SchemaViolation> into)
    {
        if (node.Errors is not null)
        {
            foreach (var (keyword, message) in node.Errors)
            {
                into.Add(new SchemaViolation(
                    node.InstanceLocation.ToString(),
                    $"[{keyword}] {message}"));
            }
        }

        if (node.Details is null) return;

        foreach (var detail in node.Details)
        {
            Collect(detail, into);
        }
    }
}
