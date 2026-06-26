using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using PayBridge.Common.Contracts;
using PayBridge.PaymentApi.Observability;
using RabbitMQ.Client;

namespace PayBridge.PaymentApi.Messaging;

/// <summary>
/// Publishes payment lifecycle events to RabbitMQ.
///
/// Key responsibility beyond serialization: manually injecting the
/// current trace context (W3C traceparent) into message headers so
/// the consumer can continue the trace.
/// HTTP and gRPC clients do this automatically; messaging libraries
/// do not, because the producer/consumer relationship is async and
/// looser than RPC.
/// </summary>
public class PaymentEventPublisher : IAsyncDisposable
{
    public const string ExchangeName = "paybridge.payments";
    public const string QueueName = "paybridge.payments.settlement";
    public const string RoutingKey = "payment.completed";

    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly ILogger<PaymentEventPublisher> _logger;

    private PaymentEventPublisher(
        IConnection connection,
        IChannel channel,
        ILogger<PaymentEventPublisher> logger)
    {
        _connection = connection;
        _channel = channel;
        _logger = logger;
    }

    public static async Task<PaymentEventPublisher> CreateAsync(
        IConfiguration configuration,
        ILogger<PaymentEventPublisher> logger)
    {
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMq:Host"] ?? "localhost",
            Port = int.Parse(configuration["RabbitMq:Port"] ?? "5672"),
            UserName = configuration["RabbitMq:User"] ?? "guest",
            Password = configuration["RabbitMq:Password"] ?? "guest"
        };

        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        // Declare exchange + queue + binding. Idempotent — safe to re-run.
        await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(QueueName, ExchangeName, RoutingKey);

        return new PaymentEventPublisher(connection, channel, logger);
    }

    public async Task PublishAsync(PaymentEvent evt, CancellationToken ct = default)
    {
        // Start a custom Producer span for this publish operation.
        // This is what the consumer will see as the parent of its own span.
        using var activity = PaymentTracing.Source.StartActivity(
            name: $"{RoutingKey} publish",
            kind: ActivityKind.Producer);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination", ExchangeName);
        activity?.SetTag("messaging.destination_kind", "topic");
        activity?.SetTag("messaging.routing_key", RoutingKey);
        activity?.SetTag("payment.id", evt.PaymentId.ToString());

        var props = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>()
        };

        // --- Manual trace context propagation ---
        // Inject the current Activity's W3C trace context (traceparent)
        // into the message headers using OpenTelemetry's text-map propagator.
        // Consumer will extract from these same headers.
        if (activity is not null)
        {
            Propagator.Inject(
                new PropagationContext(activity.Context, Baggage.Current),
                props.Headers,
                InjectTraceContext);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(evt);

        await _channel.BasicPublishAsync(
            exchange: ExchangeName,
            routingKey: RoutingKey,
            mandatory: true,
            basicProperties: props,
            body: body,
            cancellationToken: ct);

        _logger.LogInformation(
            "Payment event published. PaymentId={PaymentId} EventType={EventType}",
            evt.PaymentId, evt.EventType);
    }

    private static void InjectTraceContext(IDictionary<string, object?> headers, string key, string value)
    {
        headers[key] = Encoding.UTF8.GetBytes(value);
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}