using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using PayBridge.Common.Contracts;
using PayBridge.SettlementConsumer.Data;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PayBridge.SettlementConsumer;

public class SettlementWorker : BackgroundService
{
    private const string QueueName = "paybridge.payments.settlement";
    public const string ActivitySourceName = "PayBridge.SettlementConsumer";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SettlementWorker> _logger;

    private IConnection? _connection;
    private IChannel? _channel;

    public SettlementWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<SettlementWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _configuration["RabbitMq:Host"] ?? "localhost",
            Port = int.Parse(_configuration["RabbitMq:Port"] ?? "5672"),
            UserName = _configuration["RabbitMq:User"] ?? "guest",
            Password = _configuration["RabbitMq:Password"] ?? "guest"
        };

        _connection = await factory.CreateConnectionAsync(stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declare the same queue as the publisher. Idempotent.
        await _channel.QueueDeclareAsync(
            QueueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: stoppingToken);

        // QoS: don't deliver another message to this consumer until it ACKs.
        // Limits memory pressure and lets the broker rebalance under load.
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false,   // Manual ack so we can NACK on failure.
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation("Settlement worker started. Listening on {Queue}", QueueName);

        // Block until shutdown
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        // --- Manual trace context extraction ---
        // Pull the W3C trace context out of message headers and start
        // our consumer span as a child of the publisher's span.
        var parentContext = Propagator.Extract(
            default,
            ea.BasicProperties.Headers ?? new Dictionary<string, object?>(),
            ExtractTraceContext);

        Baggage.Current = parentContext.Baggage;

        using var activity = ActivitySource.StartActivity(
            $"{QueueName} receive",
            kind: ActivityKind.Consumer,
            parentContext: parentContext.ActivityContext);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination", QueueName);
        activity?.SetTag("messaging.destination_kind", "queue");
        activity?.SetTag("messaging.operation", "receive");

        try
        {
            var body = ea.Body.ToArray();
            var evt = JsonSerializer.Deserialize<PaymentEvent>(body);
            if (evt is null)
            {
                _logger.LogWarning("Dropped malformed message. DeliveryTag={Tag}", ea.DeliveryTag);
                await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            activity?.SetTag("payment.id", evt.PaymentId.ToString());
            activity?.SetTag("payment.merchant_id", evt.MerchantId);
            activity?.SetTag("event.type", evt.EventType);

            await PersistSettlementAsync(evt);

            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);

            _logger.LogInformation(
                "Settlement persisted. PaymentId={PaymentId} EventType={EventType}",
                evt.PaymentId, evt.EventType);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Failed to process settlement. DeliveryTag={Tag}", ea.DeliveryTag);

            // Requeue=false so we don't infinitely retry. In production
            // we'd route to a DLQ here — described in design doc.
            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private async Task PersistSettlementAsync(PaymentEvent evt)
    {
        // Resolve a scoped DbContext — DbContext is not thread-safe;
        // a fresh one per message is the right pattern.
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettlementDbContext>();

        var record = new SettlementRecord
        {
            PaymentId = evt.PaymentId,
            MerchantId = evt.MerchantId,
            Amount = evt.Amount,
            Currency = evt.Currency,
            FinalStatus = evt.EventType == "PaymentCompleted"
                ? PaymentStatus.Completed
                : PaymentStatus.Failed,
            ProviderTransactionId = evt.ProviderTransactionId,
            EventTimestamp = evt.Timestamp,
            PersistedAt = DateTime.UtcNow
        };

        db.Settlements.Add(record);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Idempotent: duplicate delivery, already settled. ACK and move on.
            _logger.LogInformation(
                "Duplicate settlement ignored. PaymentId={PaymentId}", evt.PaymentId);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";

    private static IEnumerable<string> ExtractTraceContext(
        IDictionary<string, object?> headers, string key)
    {
        if (headers.TryGetValue(key, out var value) && value is byte[] bytes)
        {
            return new[] { Encoding.UTF8.GetString(bytes) };
        }
        return Array.Empty<string>();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}