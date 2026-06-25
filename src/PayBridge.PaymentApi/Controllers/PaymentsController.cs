using Microsoft.AspNetCore.Mvc;
using PayBridge.Common.Contracts;

namespace PayBridge.PaymentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentsController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreatePaymentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MerchantId))
            return BadRequest(new { error = "merchantId is required" });
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return BadRequest(new { error = "idempotencyKey is required" });
        if (request.Amount <= 0)
            return BadRequest(new { error = "amount must be positive" });

        var paymentId = Guid.NewGuid();

        _logger.LogInformation(
            "Payment received. PaymentId={PaymentId} MerchantId={MerchantId} Amount={Amount} Currency={Currency}",
            paymentId, request.MerchantId, request.Amount, request.Currency);

        // === Fraud check (cross-service call over HTTP) ===
        var fraudClient = _httpClientFactory.CreateClient("fraud");
        var fraudReq = new FraudCheckRequest(
            paymentId,
            request.MerchantId,
            request.Amount,
            request.Currency,
            request.CustomerEmail,
            request.Method);

        FraudCheckResponse? fraudResp;
        try
        {
            var httpResp = await fraudClient.PostAsJsonAsync("/api/fraud/check", fraudReq, ct);
            httpResp.EnsureSuccessStatusCode();
            fraudResp = await httpResp.Content.ReadFromJsonAsync<FraudCheckResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Fraud service call failed. PaymentId={PaymentId}", paymentId);
            return StatusCode(503, new { error = "fraud service unavailable", paymentId });
        }

        if (fraudResp is null)
            return StatusCode(502, new { error = "invalid fraud response", paymentId });

        if (!fraudResp.Approved)
        {
            _logger.LogWarning(
                "Payment rejected by fraud. PaymentId={PaymentId} RiskScore={RiskScore} Reason={Reason}",
                paymentId, fraudResp.RiskScore, fraudResp.Reason);

            return Ok(new CreatePaymentResponse(
                paymentId, PaymentStatus.Failed, DateTime.UtcNow));
        }

        return Accepted(new CreatePaymentResponse(
            paymentId, PaymentStatus.Submitted, DateTime.UtcNow));
    }
}