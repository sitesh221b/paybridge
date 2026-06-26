using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
using PayBridge.PaymentApi.Data;
using StackExchange.Redis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Polly;

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
    .AddGrpcClientInstrumentation()
    .AddEntityFrameworkCoreInstrumentation()
    .AddRedisInstrumentation()
    .AddOtlpExporter());

otel.WithMetrics(m => m
    .AddMeter(PayBridge.PaymentApi.Observability.PaymentMetrics.MeterName)
    .AddMeter("Polly")
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

builder.Services.AddSingleton<PayBridge.PaymentApi.Observability.PaymentMetrics>();

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

// === Fraud gRPC client ===
builder.Services.AddGrpcClient<PayBridge.Common.Grpc.FraudDetection.FraudDetectionClient>(o =>
{
    o.Address = new Uri(fraudBaseUrl);
})
.AddResilienceHandler("fraud-pipeline", (pipeline, context) =>
{
    var metrics = context.ServiceProvider
        .GetRequiredService<PayBridge.PaymentApi.Observability.PaymentMetrics>();
    var logger = context.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("FraudResilience");

    pipeline
        .AddTimeout(TimeSpan.FromSeconds(2))
        .AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(200)
        })
        .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(10),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(15),
            OnOpened = args =>
            {
                metrics.RecordBreakerEvent("opened");
                logger.LogWarning("Fraud circuit breaker OPENED");
                return ValueTask.CompletedTask;
            },
            OnClosed = args =>
            {
                metrics.RecordBreakerEvent("closed");
                logger.LogInformation("Fraud circuit breaker CLOSED — service recovered");
                return ValueTask.CompletedTask;
            },
            OnHalfOpened = args =>
            {
                metrics.RecordBreakerEvent("half_opened");
                return ValueTask.CompletedTask;
            }
        });
});

var providerBaseUrl = builder.Configuration["Services:Provider:BaseUrl"]
                       ?? "http://localhost:5003";

builder.Services.AddHttpClient("provider", client =>
{
    client.BaseAddress = new Uri(providerBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

// === Health checks ===
builder.Services.AddHealthChecks()
    // Postgres = critical: if it's down, we cannot fulfil the contract
    .AddNpgSql(
        connectionString,
        name: "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: new[] { "ready", "critical" })
    // Redis = non-critical: we fall back to DB; service remains usable
    .AddRedis(
        redisConnection,
        name: "redis",
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "ready" })
    // Downstream fraud check = non-critical for liveness; track for readiness signal
    .AddUrlGroup(
        new Uri($"{fraudBaseUrl}/health/live"),
        name: "fraud-stub",
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "ready" });

var app = builder.Build();

app.MapControllers();

// Liveness: am I alive? Don't depend on anything external —
// a transient DB blip should not cause a restart loop.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,  // run no checks, just confirm the process responds
    ResponseWriter = WriteHealthJson
});

// Readiness: am I ready to serve traffic? Critical deps must pass.
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthJson
});

// Simple liveness probe — we'll formalize health checks later
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();

static Task WriteHealthJson(HttpContext ctx, HealthReport report)
{
    ctx.Response.ContentType = "application/json";
    var payload = new
    {
        status = report.Status.ToString(),
        totalDuration = report.TotalDuration.TotalMilliseconds,
        results = report.Entries.ToDictionary(
            e => e.Key,
            e => new
            {
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                tags = e.Value.Tags,
                exception = e.Value.Exception?.Message
            })
    };
    return ctx.Response.WriteAsJsonAsync(payload);
}