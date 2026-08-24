using AutoHous.Revenue.Api;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var connectionString =
    builder.Configuration["REVENUE_DB_CONNECTION"]
    ?? Environment.GetEnvironmentVariable("REVENUE_DB_CONNECTION")
    ?? throw new InvalidOperationException("REVENUE_DB_CONNECTION nao configurada. Ver .env.example.");

// Antes de qualquer servico: sem credencial utilizavel o processo nao sobe.
// Ver RevenueApiKeys - a mesma regra que o gateway do Hermes aplica a si mesmo.
var apiKeys = RevenueApiKeys.Load(builder.Configuration);

builder.Services.AddRevenueInfrastructure(connectionString);
builder.Services.AddRevenueUseCases();
builder.Services.AddProblemDetails();

// Enums como texto: "researched" e legivel e estavel; o ordinal 2 quebra
// silenciosamente qualquer consumidor quando um valor novo entra no meio do enum.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.SnakeCaseLower)));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRevenueApiKey(apiKeys);

app.MapRevenueEndpoints();

Log.Information(
    "Revenue API protegida por API key ({Count} chave(s) ativa(s); /health aberto).",
    apiKeys.Count);

app.Run();

/// <summary>Exposto para o WebApplicationFactory dos testes de integracao.</summary>
public partial class Program;
