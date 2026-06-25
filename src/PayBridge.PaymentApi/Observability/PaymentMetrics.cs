using System.Diagnostics;
using System.Diagnostics.Metrics;
using PayBridge.Common.Contracts;

namespace PayBridge.PaymentApi.Observability;

/// <summary>
/// Business metrics for the Payment API. Cardinality-bounded by design:
/// labels are closed sets (status, method, currency, outcome) — never
/// payment IDs, emails, or free-form strings.
/// </summary>
public class PaymentMetrics
{
    public const string MeterName = "PayBridge.PaymentApi";

    private readonly Counter<long> _paymentsTotal;
    private readonly Histogram<double> _paymentDurationMs;
    private readonly Counter<long> _fraudOutcomes;
    private readonly Counter<long> _idempotencyHits;
    private readonly UpDownCounter<long> _inFlightPayments;
    private readonly Counter<long> _breakerEvents;

    public PaymentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _paymentsTotal = meter.CreateCounter<long>(
            "paybridge.payments.total",
            unit: "{payment}",
            description: "Total payments processed, by outcome");

        _paymentDurationMs = meter.CreateHistogram<double>(
            "paybridge.payments.duration",
            unit: "ms",
            description: "End-to-end payment processing duration");

        _fraudOutcomes = meter.CreateCounter<long>(
            "paybridge.fraud.outcomes",
            unit: "{check}",
            description: "Fraud check outcomes");

        _idempotencyHits = meter.CreateCounter<long>(
            "paybridge.idempotency.replays",
            unit: "{replay}",
            description: "Duplicate requests served from idempotency cache or DB");

        _inFlightPayments = meter.CreateUpDownCounter<long>(
            "paybridge.payments.in_flight",
            unit: "{payment}",
            description: "Currently-processing payments");

        _breakerEvents = meter.CreateCounter<long>(
            "paybridge.resilience.breaker_events",
            unit: "{event}",
            description: "Circuit breaker state transitions");
    }

    public void RecordPayment(PaymentStatus status, PaymentMethod method, string currency, double durationMs)
    {
        var tags = new TagList
        {
            { "status", status.ToString() },
            { "method", method.ToString() },
            { "currency", currency }
        };
        _paymentsTotal.Add(1, tags);
        _paymentDurationMs.Record(durationMs, tags);
    }

    public void RecordFraudOutcome(bool approved)
    {
        _fraudOutcomes.Add(1, new KeyValuePair<string, object?>(
            "outcome", approved ? "approved" : "rejected"));
    }

    public void RecordIdempotencyReplay(string source)  // "cache" or "db"
    {
        _idempotencyHits.Add(1, new KeyValuePair<string, object?>("source", source));
    }

    public IDisposable TrackInFlight()
    {
        _inFlightPayments.Add(1);
        return new InFlightScope(_inFlightPayments);
    }

    private sealed class InFlightScope : IDisposable
    {
        private readonly UpDownCounter<long> _counter;
        public InFlightScope(UpDownCounter<long> counter) => _counter = counter;
        public void Dispose() => _counter.Add(-1);
    }

    public void RecordBreakerEvent(string transition)  // "opened", "closed", "half_opened"
    {
        _breakerEvents.Add(1, new KeyValuePair<string, object?>("transition", transition));
    }
}