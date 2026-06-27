using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PayBridge.Common.Contracts;
using PayBridge.Common.Grpc;
using PayBridge.PaymentApi.Data;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayBridge.PaymentApi.Tests.IntegrationTests;

/// <summary>
/// Integration tests for the Payment API against real Postgres + Redis
/// containers via Testcontainers. The gRPC fraud client and the HTTP
/// provider client are replaced with in-process fakes — we test the
/// orchestration, not the external services.
/// </summary>
public class PaymentApiIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("paybridge_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private WebApplicationFactory<Program> _factory = default!;
    private HttpClient _client = default!;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _redis.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Point the app at our test containers.
                builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
                builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());

                // The webhook URL is irrelevant for these tests; the provider call is faked.
                builder.UseSetting("Services:Provider:BaseUrl", "http://test-provider");
                builder.UseSetting("WebhookBaseUrl", "http://test");

                // Disable the RabbitMQ publisher and webhook publishing — out of
                // scope for these tests. We assert the synchronous request path
                // and the persisted state.
                builder.UseSetting("RabbitMq:Host", "disabled");

                builder.ConfigureServices(services =>
                {
                    // Replace the gRPC FraudDetectionClient with a fake.
                    RemoveAll(services, typeof(FraudDetection.FraudDetectionClient));
                    services.AddSingleton(new FraudDetection.FraudDetectionClient(
                        new FakeFraudCallInvoker(approved: true)));

                    // Replace the named provider HttpClient with one that always returns 202.
                    services.AddHttpClient("provider")
                        .ConfigurePrimaryHttpMessageHandler(() => new FakeProviderHandler());

                    // Replace the RabbitMQ publisher with a no-op so we don't need a broker
                    // for these tests. Cast through object to bypass the constructor.
                    var publisherDescriptor = services.FirstOrDefault(d =>
                        d.ServiceType.Name == "PaymentEventPublisher");
                    if (publisherDescriptor is not null) services.Remove(publisherDescriptor);
                });
            });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private static void RemoveAll(IServiceCollection services, Type t)
    {
        var matches = services.Where(d => d.ServiceType == t).ToList();
        foreach (var m in matches) services.Remove(m);
    }

    [Fact]
    public async Task POST_payments_persists_to_database()
    {
        var req = SamplePayment("merchant-1", "happy-001");

        var resp = await _client.PostAsJsonAsync("/api/payments", req);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<CreatePaymentResponse>(JsonOpts);
        body.Should().NotBeNull();
        body!.Status.Should().Be(PaymentStatus.Submitted);

        // Real DB check — proves the payment was persisted by the actual EF Core path.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var stored = await db.Payments.FindAsync(body.PaymentId);
        stored.Should().NotBeNull();
        stored!.MerchantId.Should().Be("merchant-1");
        stored.IdempotencyKey.Should().Be("happy-001");
    }

    [Fact]
    public async Task Duplicate_idempotency_key_returns_original_payment()
    {
        var req = SamplePayment("merchant-1", "dup-001");

        var first = await _client.PostAsJsonAsync("/api/payments", req);
        var firstBody = await first.Content.ReadFromJsonAsync<CreatePaymentResponse>(JsonOpts);

        var second = await _client.PostAsJsonAsync("/api/payments", req);
        var secondBody = await second.Content.ReadFromJsonAsync<CreatePaymentResponse>(JsonOpts);

        // Same id returned — never double-processed.
        secondBody!.PaymentId.Should().Be(firstBody!.PaymentId);

        // Only ONE row in the database — the DB unique constraint is the durable truth.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var count = await db.Payments.CountAsync(p =>
            p.MerchantId == "merchant-1" && p.IdempotencyKey == "dup-001");
        count.Should().Be(1);
    }

    [Fact]
    public async Task Same_idempotency_key_across_merchants_creates_distinct_payments()
    {
        // Idempotency is scoped per-merchant: (merchant_id, idempotency_key) is the key,
        // not idempotency_key alone. Two different merchants reusing the same key must
        // produce two separate payments.
        var reqA = SamplePayment("merchant-a", "shared-key");
        var reqB = SamplePayment("merchant-b", "shared-key");

        var respA = await _client.PostAsJsonAsync("/api/payments", reqA);
        var respB = await _client.PostAsJsonAsync("/api/payments", reqB);

        var bodyA = await respA.Content.ReadFromJsonAsync<CreatePaymentResponse>(JsonOpts);
        var bodyB = await respB.Content.ReadFromJsonAsync<CreatePaymentResponse>(JsonOpts);

        bodyA!.PaymentId.Should().NotBe(bodyB!.PaymentId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var rows = await db.Payments.CountAsync(p => p.IdempotencyKey == "shared-key");
        rows.Should().Be(2);
    }

    [Fact]
    public async Task Fraud_rejection_persists_failed_status()
    {
        // Build a separate factory with a fraud client that REJECTS.
        using var rejectingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                RemoveAll(services, typeof(FraudDetection.FraudDetectionClient));
                services.AddSingleton(new FraudDetection.FraudDetectionClient(
                    new FakeFraudCallInvoker(approved: false)));
            });
        });
        using var client = rejectingFactory.CreateClient();

        var req = SamplePayment("merchant-1", "rejected-001");
        var resp = await client.PostAsJsonAsync("/api/payments", req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);  // Ok, not Accepted — rejected, not submitted.
        var body = await resp.Content.ReadFromJsonAsync<CreatePaymentResponse>(JsonOpts);
        body!.Status.Should().Be(PaymentStatus.Failed);

        using var scope = rejectingFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var stored = await db.Payments.FindAsync(body.PaymentId);
        stored!.Status.Should().Be(PaymentStatus.Failed);
        stored.FailureReason.Should().NotBeNullOrEmpty();
    }

    private static CreatePaymentRequest SamplePayment(string merchant, string idempotencyKey) =>
        new(merchant, idempotencyKey, 49.99m, "USD", "[email protected]",
            PaymentMethod.CreditCard, null);
}

/// <summary>
/// Fakes the gRPC client by intercepting its underlying CallInvoker.
/// Returns a configurable approve/reject response without touching the network.
/// </summary>
internal class FakeFraudCallInvoker : CallInvoker
{
    private readonly bool _approved;

    public FakeFraudCallInvoker(bool approved) => _approved = approved;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var response = (TResponse)(object)new FraudCheckResponse
        {
            Approved = _approved,
            RiskScore = _approved ? 0.1 : 0.95,
            Reason = _approved ? "ok" : "high_risk_score"
        };

        return new AsyncUnaryCall<TResponse>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    // Other methods unused in this test — throw if called by accident.
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => throw new NotImplementedException();

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => throw new NotImplementedException();

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => throw new NotImplementedException();

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => throw new NotImplementedException();
}

/// <summary>
/// In-process HttpMessageHandler that fakes the Provider service.
/// Always returns 202 with a valid SubmitToProviderResponse.
/// </summary>
internal class FakeProviderHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = new SubmitToProviderResponse(
            ProviderTransactionId: "prov_test_" + Guid.NewGuid().ToString("N")[..12],
            Status: "ACCEPTED");

        return new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(response)
        };
    }
}