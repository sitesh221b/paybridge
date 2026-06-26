using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PayBridge.SettlementConsumer;
using PayBridge.SettlementConsumer.Data;

var builder = Host.CreateApplicationBuilder(args);

const string ServiceName = "settlement-consumer";
const string ServiceVersion = "0.1.0";

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: ServiceVersion));

otel.WithTracing(t => t
    .AddSource(SettlementWorker.ActivitySourceName)
    .AddEntityFrameworkCoreInstrumentation()
    .AddOtlpExporter());

otel.WithMetrics(m => m
    .AddRuntimeInstrumentation()
    .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName, serviceVersion: ServiceVersion));
    o.IncludeScopes = true;
    o.IncludeFormattedMessage = true;
    o.AddOtlpExporter();
});

// EF Core for the settlements DB
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=paybridge;Username=paybridge;Password=paybridge_dev";

builder.Services.AddDbContext<SettlementDbContext>(opt =>
    opt.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.AddHostedService<SettlementWorker>();

var app = builder.Build();

// Apply migrations on startup (same dev-mode pattern as payment-api).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SettlementDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();