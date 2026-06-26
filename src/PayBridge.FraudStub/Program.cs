using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PayBridge.FraudStub.Services;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "fraud-stub";
const string ServiceVersion = "0.2.0";

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: ServiceVersion));

otel.WithTracing(t => t
    .AddAspNetCoreInstrumentation()  // ASP.NET Core hosts gRPC; this captures gRPC server spans
    .AddOtlpExporter());

otel.WithMetrics(m => m
    .AddAspNetCoreInstrumentation()
    .AddRuntimeInstrumentation()
    .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName, serviceVersion: ServiceVersion));
    o.IncludeScopes = true;
    o.IncludeFormattedMessage = true;
    o.AddOtlpExporter();
});

// gRPC server registration
builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<FraudDetectionService>();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

app.Run();