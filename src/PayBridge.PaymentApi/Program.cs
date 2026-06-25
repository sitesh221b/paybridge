using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
using PayBridge.PaymentApi.Data;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// --- Service identity for telemetry ---
const string ServiceName = "payment-api";
const string ServiceVersion = "0.1.0";

// --- OpenTelemetry: traces + metrics + logs ---
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: ServiceVersion));

otel.WithTracing(t => t
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddEntityFrameworkCoreInstrumentation()
    .AddRedisInstrumentation()
    .AddOtlpExporter());

otel.WithMetrics(m => m
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()
    .AddOtlpExporter());

// Logs are wired through the ILogger pipeline
builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName, serviceVersion: ServiceVersion));
    o.IncludeScopes = true;
    o.IncludeFormattedMessage = true;
    o.AddOtlpExporter();
});

// --- ASP.NET Core services ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();

// === Database (Postgres) ===
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=paybridge;Username=paybridge;Password=paybridge_dev";

builder.Services.AddDbContext<PaymentDbContext>(opt =>
    opt.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

// === Redis ===
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

// Configuration: where to find downstream services
var fraudBaseUrl = builder.Configuration["Services:FraudStub:BaseUrl"]
                   ?? "http://localhost:5002";

builder.Services.AddHttpClient("fraud", client =>
{
    client.BaseAddress = new Uri(fraudBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

app.MapControllers();

// Simple liveness probe — we'll formalize health checks later
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

app.Run();