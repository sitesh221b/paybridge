using Microsoft.AspNetCore.Mvc;
using PayBridge.Common.Contracts;

namespace PayBridge.PaymentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(ILogger<PaymentsController> logger)
    {
        _logger = logger;
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreatePaymentRequest request)
    {
        // Basic validation
        if (string.IsNullOrWhiteSpace(request.MerchantId))
            return BadRequest(new { error = "merchantId is required" });
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return BadRequest(new { error = "idempotencyKey is required" });
        if (request.Amount <= 0)
            return BadRequest(new { error = "amount must be positive" });

        var paymentId = Guid.NewGuid();

        _logger.LogInformation(
            "Payment created. PaymentId={PaymentId} MerchantId={MerchantId} Amount={Amount} Currency={Currency}",
            paymentId, request.MerchantId, request.Amount, request.Currency);

        var response = new CreatePaymentResponse(
            PaymentId: paymentId,
            Status: PaymentStatus.Created,
            CreatedAt: DateTime.UtcNow
        );

        return Accepted(response);
    }
}