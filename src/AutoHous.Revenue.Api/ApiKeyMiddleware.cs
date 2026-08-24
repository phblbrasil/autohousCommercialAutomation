namespace AutoHous.Revenue.Api;

/// <summary>
/// Exige <c>Authorization: Bearer &lt;chave&gt;</c> em tudo, menos nas rotas
/// declaradas como anonimas.
///
/// Middleware e nao filtro de endpoint por um motivo de HML/PRD: rota nova entra
/// protegida por padrao. Um filtro precisa ser lembrado a cada
/// <c>MapPost</c> - e o dia em que alguem esquecer nao aparece em teste nenhum.
/// </summary>
public static class ApiKeyMiddleware
{
    /// <summary>
    /// <c>/health</c> fica aberto: probe de liveness de orquestrador roda sem
    /// credencial, e o que ele devolve - "banco alcancavel" - ja e observavel de
    /// fora pelo simples fato de a API responder.
    /// </summary>
    public static readonly string[] AnonymousPaths = ["/health"];

    public static IApplicationBuilder UseRevenueApiKey(
        this IApplicationBuilder app, RevenueApiKeys keys, params string[] anonymousPaths)
    {
        var anonymous = (anonymousPaths.Length > 0 ? anonymousPaths : AnonymousPaths)
            .Select(p => new PathString(p))
            .ToArray();

        return app.Use(async (context, next) =>
        {
            if (anonymous.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next(context);
                return;
            }

            if (keys.Matches(Bearer(context.Request)))
            {
                await next(context);
                return;
            }

            // Log sem a chave apresentada: registrar tentativa e util, registrar
            // o segredo tentado transforma o log num deposito de credencial.
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(ApiKeyMiddleware))
                .LogWarning(
                    "Requisicao sem credencial valida: {Method} {Path} de {Remote}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";

            await context.RequestServices
                .GetRequiredService<IProblemDetailsService>()
                .WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Title = "Credencial ausente ou invalida",
                        Status = StatusCodes.Status401Unauthorized,
                        Detail = "Envie Authorization: Bearer <REVENUE_API_KEY>."
                    }
                });
        });
    }

    private static string? Bearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}
