using System.Diagnostics;
using PayBridge.PaymentApi.Observability;
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
    private readonly PayBridge.Common.Grpc.FraudDetection.FraudDetectionClient _fraudClient;
    private readonly ILogger<PaymentsController> _logger;
    private readonly PaymentMetrics _metrics;

    public PaymentsController(
        PaymentDbContext db,
        IConnectionMultiplexer redis,
        PayBridge.Common.Grpc.FraudDetection.FraudDetectionClient fraudClient,
        ILogger<PaymentsController> logger,
        PaymentMetrics metrics)
    {
        _db = db;
        _redis = redis;
        _fraudClient = fraudClient;
        _logger = logger;
        _metrics = metrics;
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

        var stopwatch = Stopwatch.StartNew();
        using var _ = _metrics.TrackInFlight();

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
            {
                _metrics.RecordIdempotencyReplay("cache");
                _metrics.RecordPayment(existing.Status, existing.Method, existing.Currency, stopwatch.Elapsed.TotalMilliseconds);
                return Ok(ToResponse(existing));
            }
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
            _metrics.RecordIdempotencyReplay("db");
            _metrics.RecordPayment(dbExisting.Status, dbExisting.Method, dbExisting.Currency, stopwatch.Elapsed.TotalMilliseconds);
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
            _metrics.RecordIdempotencyReplay("db");
            _metrics.RecordPayment(winner.Status, winner.Method, winner.Currency, stopwatch.Elapsed.TotalMilliseconds);
            return Ok(ToResponse(winner));
        }

        await cache.StringSetAsync(idempotencyCacheKey, payment.Id.ToString(), IdempotencyTtl);

        _logger.LogInformation(
            "Payment created. PaymentId={PaymentId} MerchantId={MerchantId} Amount={Amount} Currency={Currency}",
            payment.Id, payment.MerchantId, payment.Amount, payment.Currency);

        // === Fraud check ===
        PayBridge.Common.Grpc.FraudCheckResponse fraudResp;
        try
        {
            fraudResp = await _fraudClient.CheckTransactionAsync(
                new PayBridge.Common.Grpc.FraudCheckRequest
                {
                    PaymentId = payment.Id.ToString(),
                    MerchantId = payment.MerchantId,
                    Amount = (double)payment.Amount,
                    Currency = payment.Currency,
                    CustomerEmail = request.CustomerEmail,
                    PaymentMethod = payment.Method.ToString()
                },
                cancellationToken: ct);
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogError(ex, "Fraud service gRPC call failed. PaymentId={PaymentId} Status={Status}",
                payment.Id, ex.StatusCode);
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = "fraud_service_unavailable";
            payment.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _metrics.RecordPayment(payment.Status, payment.Method, payment.Currency,
                stopwatch.Elapsed.TotalMilliseconds);
            return StatusCode(503, ToResponse(payment));
        }

        if (!fraudResp.Approved)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = fraudResp.Reason;
            payment.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _metrics.RecordFraudOutcome(approved: false);
            _metrics.RecordPayment(payment.Status, payment.Method, payment.Currency,
                stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogWarning(
                "Payment rejected by fraud. PaymentId={PaymentId} RiskScore={RiskScore} Reason={Reason}",
                payment.Id, fraudResp.RiskScore, fraudResp.Reason);
            return Ok(ToResponse(payment));
        }

        payment.Status = PaymentStatus.Submitted;
        await _db.SaveChangesAsync(ct);

        _metrics.RecordFraudOutcome(approved: true);
        _metrics.RecordPayment(payment.Status, payment.Method, payment.Currency, stopwatch.Elapsed.TotalMilliseconds);
        return Accepted(ToResponse(payment));
    }

    private static CreatePaymentResponse ToResponse(Payment p) =>
        new(p.Id, p.Status, p.CreatedAt);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
}