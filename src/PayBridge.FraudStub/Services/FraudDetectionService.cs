using Grpc.Core;
using PayBridge.Common.Grpc;

namespace PayBridge.FraudStub.Services;

public class FraudDetectionService : FraudDetection.FraudDetectionBase
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<FraudDetectionService> _logger;

    public FraudDetectionService(ILogger<FraudDetectionService> logger)
    {
        _logger = logger;
    }

    public override async Task<FraudCheckResponse> CheckTransaction(
        FraudCheckRequest request,
        ServerCallContext context)
    {
        // Simulate latency so traces show duration
        await Task.Delay(Rng.Next(20, 80), context.CancellationToken);

        var riskScore = Rng.NextDouble();
        var approved = riskScore < 0.85;  // ~15% rejection rate
        var reason = approved ? "ok" : "high_risk_score";

        _logger.LogInformation(
            "Fraud check completed. PaymentId={PaymentId} Approved={Approved} RiskScore={RiskScore:F3}",
            request.PaymentId, approved, riskScore);

        return new FraudCheckResponse
        {
            Approved = approved,
            RiskScore = riskScore,
            Reason = reason
        };
    }
}