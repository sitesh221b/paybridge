using Microsoft.AspNetCore.Mvc;
using PayBridge.Common.Contracts;

namespace PayBridge.ProviderStub.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProviderController : ControllerBase
{
    private static readonly Random Rng = Random.Shared;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProviderController> _logger;

    public ProviderController(
        IHttpClientFactory httpClientFactory,
        ILogger<ProviderController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpPost("submit")]
    public IActionResult Submit([FromBody] SubmitToProviderRequest request)
    {
        var providerTxnId = $"prov_{Guid.NewGuid():N}"[..20];

        _logger.LogInformation(
            "Provider received submission. PaymentId={PaymentId} ProviderTxnId={ProviderTxnId} Amount={Amount}",
            request.PaymentId, providerTxnId, request.Amount);

        // Fire-and-forget the async webhook callback.
        // Intentional: this models how real providers work — accept now, callback later.
        _ = Task.Run(async () =>
        {
            // Explicitly clear the ambient Activity so this background work
            // doesn't inherit the inbound request's trace context.
            System.Diagnostics.Activity.Current = null;
            
            try
            {
                await FireWebhookAsync(providerTxnId, request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Webhook callback failed. ProviderTxnId={ProviderTxnId}", providerTxnId);
            }
        });

        return Accepted(new SubmitToProviderResponse(providerTxnId, "ACCEPTED"));
    }

    private async Task FireWebhookAsync(string providerTxnId, SubmitToProviderRequest request)
    {
        // Simulate provider processing time
        await Task.Delay(Rng.Next(500, 1500));

        // ~90% success rate
        var success = Rng.NextDouble() < 0.9;
        var status = success ? "SUCCESS" : "FAILED";

        var callback = new ProviderWebhookCallback(
            ProviderTransactionId: providerTxnId,
            Status: status,
            Timestamp: DateTime.UtcNow,
            Metadata: new Dictionary<string, string>
            {
                { "paybridge_payment_id", request.PaymentId.ToString() }
            }
        );

        var client = _httpClientFactory.CreateClient("webhook");
        var resp = await client.PostAsJsonAsync(request.WebhookUrl, callback);

        _logger.LogInformation(
            "Webhook callback fired. ProviderTxnId={ProviderTxnId} Status={Status} HttpStatus={HttpStatus}",
            providerTxnId, status, (int)resp.StatusCode);
    }
}