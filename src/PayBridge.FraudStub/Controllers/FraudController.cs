using Microsoft.AspNetCore.Mvc;
using PayBridge.Common.Contracts;

namespace PayBridge.FraudStub.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FraudController : ControllerBase
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<FraudController> _logger;

    public FraudController(ILogger<FraudController> logger)
    {
        _logger = logger;
    }

    [HttpPost("check")]
    public async Task<IActionResult> Check([FromBody] FraudCheckRequest request)
    {
        // Simulate real-world latency so traces actually show duration.
        await Task.Delay(Rng.Next(20, 80));

        var riskScore = Rng.NextDouble();
        var approved = riskScore < 0.85;  // ~15% rejection rate
        var reason = approved ? "ok" : "high_risk_score";

        _logger.LogInformation(
            "Fraud check completed. PaymentId={PaymentId} Approved={Approved} RiskScore={RiskScore:F3}",
            request.PaymentId, approved, riskScore);

        return Ok(new FraudCheckResponse(approved, riskScore, reason));
    }
}