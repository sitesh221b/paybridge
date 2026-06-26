using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "provider-stub";
const string ServiceVersion = "0.1.0";

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: ServiceVersion));

otel.WithTracing(t => t
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddOtlpExporter());

otel.WithMetrics(m => m
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()
    .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName, serviceVersion: ServiceVersion));
    o.IncludeScopes = true;
    o.IncludeFormattedMessage = true;
    o.AddOtlpExporter();
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// HttpClient for firing webhooks back to the payment API
builder.Services.AddHttpClient("webhook", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.Run();