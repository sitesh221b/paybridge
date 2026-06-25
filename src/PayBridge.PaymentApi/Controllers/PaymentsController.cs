using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayBridge.Common.Contracts;
using PayBridge.PaymentApi.Data;
using StackExchange.Redis;

namespace PayBridge.PaymentApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    private readonly PaymentDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        PaymentDbContext db,
        IConnectionMultiplexer redis,
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentsController> logger)
    {
        _db = db;
        _redis = redis;
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

        // === Idempotency: Redis fast path ===
        var cache = _redis.GetDatabase();
        var idempotencyCacheKey = $"idem:{request.MerchantId}:{request.IdempotencyKey}";
        var cached = await cache.StringGetAsync(idempotencyCacheKey);
        if (cached.HasValue)
        {
            var cachedId = Guid.Parse(cached!.ToString());
            _logger.LogInformation(
                "Idempotent replay served from cache. PaymentId={PaymentId} Key={IdempotencyKey}",
                cachedId, request.IdempotencyKey);

            var existing = await _db.Payments.FindAsync(new object[] { cachedId }, ct);
            if (existing is not null)
                return Ok(ToResponse(existing));
        }

        // === Idempotency: durable check via DB ===
        var dbExisting = await _db.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.MerchantId == request.MerchantId
                  && p.IdempotencyKey == request.IdempotencyKey, ct);

        if (dbExisting is not null)
        {
            // Repopulate cache for next time
            await cache.StringSetAsync(idempotencyCacheKey, dbExisting.Id.ToString(), IdempotencyTtl);
            return Ok(ToResponse(dbExisting));
        }

        // === New payment ===
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = request.MerchantId,
            IdempotencyKey = request.IdempotencyKey,
            Amount = request.Amount,
            Currency = request.Currency,
            Method = request.Method,
            Status = PaymentStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        _db.Payments.Add(payment);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race: another request with the same key won between our check and insert.
            // The DB constraint saved us. Re-read and return the winner.
            _logger.LogInformation(
                "Idempotency race resolved by DB constraint. Key={IdempotencyKey}",
                request.IdempotencyKey);

            var winner = await _db.Payments
                .AsNoTracking()
                .FirstAsync(
                    p => p.MerchantId == request.MerchantId
                      && p.IdempotencyKey == request.IdempotencyKey, ct);

            await cache.StringSetAsync(idempotencyCacheKey, winner.Id.ToString(), IdempotencyTtl);
            return Ok(ToResponse(winner));
        }

        await cache.StringSetAsync(idempotencyCacheKey, payment.Id.ToString(), IdempotencyTtl);

        _logger.LogInformation(
            "Payment created. PaymentId={PaymentId} MerchantId={MerchantId} Amount={Amount} Currency={Currency}",
            payment.Id, payment.MerchantId, payment.Amount, payment.Currency);

        // === Fraud check ===
        var fraudClient = _httpClientFactory.CreateClient("fraud");
        FraudCheckResponse? fraudResp;
        try
        {
            var httpResp = await fraudClient.PostAsJsonAsync("/api/fraud/check",
                new FraudCheckRequest(payment.Id, payment.MerchantId, payment.Amount,
                                       payment.Currency, request.CustomerEmail, payment.Method), ct);
            httpResp.EnsureSuccessStatusCode();
            fraudResp = await httpResp.Content.ReadFromJsonAsync<FraudCheckResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fraud service call failed. PaymentId={PaymentId}", payment.Id);
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = "fraud_service_unavailable";
            payment.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return StatusCode(503, ToResponse(payment));
        }

        if (fraudResp is null || !fraudResp.Approved)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = fraudResp?.Reason ?? "invalid_fraud_response";
            payment.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Payment rejected by fraud. PaymentId={PaymentId} RiskScore={RiskScore} Reason={Reason}",
                payment.Id, fraudResp?.RiskScore, payment.FailureReason);

            return Ok(ToResponse(payment));
        }

        payment.Status = PaymentStatus.Submitted;
        await _db.SaveChangesAsync(ct);

        return Accepted(ToResponse(payment));
    }

    private static CreatePaymentResponse ToResponse(Payment p) =>
        new(p.Id, p.Status, p.CreatedAt);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
}