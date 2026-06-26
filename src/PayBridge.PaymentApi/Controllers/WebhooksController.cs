using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayBridge.Common.Contracts;
using PayBridge.PaymentApi.Data;
using PayBridge.PaymentApi.Observability;

namespace PayBridge.PaymentApi.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly PaymentDbContext _db;
    private readonly PaymentMetrics _metrics;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        PaymentDbContext db,
        PaymentMetrics metrics,
        ILogger<WebhooksController> logger)
    {
        _db = db;
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

        // Idempotent: if we already finalized this payment, no-op.
        if (payment.Status == PaymentStatus.Completed || payment.Status == PaymentStatus.Failed)
        {
            _logger.LogInformation(
                "Duplicate webhook ignored. PaymentId={PaymentId} CurrentStatus={Status}",
                paymentId, payment.Status);
            return Ok(new { status = "already_processed" });
        }

        payment.Status = callback.Status == "SUCCESS" ? PaymentStatus.Completed : PaymentStatus.Failed;
        payment.FailureReason = callback.Status == "SUCCESS" ? null : "provider_declined";
        payment.CompletedAt = callback.Timestamp;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Webhook processed. PaymentId={PaymentId} ProviderTxnId={ProviderTxnId} FinalStatus={FinalStatus} OriginalTraceId={OriginalTraceId}",
            payment.Id, callback.ProviderTransactionId, payment.Status, payment.OriginalTraceId);

        return Ok(new { status = "processed", paymentId = payment.Id, finalStatus = payment.Status.ToString() });
    }
}