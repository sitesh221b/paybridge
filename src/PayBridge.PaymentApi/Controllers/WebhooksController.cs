using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayBridge.Common.Contracts;
using PayBridge.PaymentApi.Data;
using PayBridge.PaymentApi.Observability;
using PayBridge.PaymentApi.Messaging;

namespace PayBridge.PaymentApi.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly PaymentDbContext _db;
    private readonly PaymentMetrics _metrics;
    private readonly ILogger<WebhooksController> _logger;
    private readonly PaymentEventPublisher _publisher;

    public WebhooksController(
        PaymentDbContext db,
        PaymentEventPublisher publisher,
        PaymentMetrics metrics,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _publisher = publisher;
        _metrics = metrics;
        _logger = logger;
    }

    [HttpPost("provider")]
    public async Task<IActionResult> ProviderWebhook(
        [FromBody] ProviderWebhookCallback callback,
        CancellationToken ct)
    {
        if (!callback.Metadata.TryGetValue("paybridge_payment_id", out var paymentIdStr)
            || !Guid.TryParse(paymentIdStr, out var paymentId))
        {
            _logger.LogWarning("Webhook missing or invalid paybridge_payment_id. ProviderTxnId={ProviderTxnId}",
                callback.ProviderTransactionId);
            return BadRequest(new { error = "missing or invalid paybridge_payment_id in metadata" });
        }

        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null)
        {
            _logger.LogWarning("Webhook for unknown payment. PaymentId={PaymentId}", paymentId);
            return NotFound(new { error = "payment not found" });
        }

        // === Trace relinking via span links ===
        // The webhook arrived as a fresh HTTP request with its own trace context.
        // The original payment trace is a separate tree. We can't make this span
        // a child of the original (different process boundaries, different "thread
        // of execution") — but we can attach a span LINK that connects the two
        // traces causally in the backend's UI.
        var links = new List<System.Diagnostics.ActivityLink>();
        if (!string.IsNullOrEmpty(payment.OriginalTraceId)
            && !string.IsNullOrEmpty(payment.OriginalSpanId))
        {
            if (System.Diagnostics.ActivityTraceId.CreateFromString(payment.OriginalTraceId) is var traceId
                && System.Diagnostics.ActivitySpanId.CreateFromString(payment.OriginalSpanId) is var spanId)
            {
                var linkedContext = new System.Diagnostics.ActivityContext(
                    traceId,
                    spanId,
                    System.Diagnostics.ActivityTraceFlags.Recorded,
                    isRemote: true);
                links.Add(new System.Diagnostics.ActivityLink(linkedContext));
            }
        }

        using var activity = PaymentTracing.Source.StartActivity(
            "webhook.process_provider_callback",
            kind: System.Diagnostics.ActivityKind.Internal,
            parentContext: default,
            tags: null,
            links: links);

        // Domain attributes on the span — helpful for filtering/debugging.
        activity?.SetTag("payment.id", payment.Id.ToString());
        activity?.SetTag("payment.merchant_id", payment.MerchantId);
        activity?.SetTag("provider.transaction_id", callback.ProviderTransactionId);
        activity?.SetTag("webhook.status", callback.Status);

        // Idempotent: if we already finalized this payment, no-op.
        if (payment.Status == PaymentStatus.Completed || payment.Status == PaymentStatus.Failed)
        {
            activity?.SetTag("webhook.outcome", "duplicate_ignored");
            _logger.LogInformation(
                "Duplicate webhook ignored. PaymentId={PaymentId} CurrentStatus={Status}",
                paymentId, payment.Status);
            return Ok(new { status = "already_processed" });
        }

        payment.Status = callback.Status == "SUCCESS" ? PaymentStatus.Completed : PaymentStatus.Failed;
        payment.FailureReason = callback.Status == "SUCCESS" ? null : "provider_declined";
        payment.CompletedAt = callback.Timestamp;
        await _db.SaveChangesAsync(ct);

        activity?.SetTag("webhook.outcome", "processed");
        activity?.SetTag("payment.final_status", payment.Status.ToString());

        await _publisher.PublishAsync(new PaymentEvent(
            PaymentId: payment.Id,
            MerchantId: payment.MerchantId,
            EventType: payment.Status == PaymentStatus.Completed ? "PaymentCompleted" : "PaymentFailed",
            Amount: payment.Amount,
            Currency: payment.Currency,
            ProviderTransactionId: payment.ProviderTransactionId,
            FailureReason: payment.FailureReason,
            Timestamp: payment.CompletedAt ?? DateTime.UtcNow
        ), ct);

        _logger.LogInformation(
            "Webhook processed. PaymentId={PaymentId} ProviderTxnId={ProviderTxnId} FinalStatus={FinalStatus} OriginalTraceId={OriginalTraceId}",
            payment.Id, callback.ProviderTransactionId, payment.Status, payment.OriginalTraceId);

        return Ok(new { status = "processed", paymentId = payment.Id, finalStatus = payment.Status.ToString() });
    }
}