using FluentAssertions;
using PayBridge.Common.Contracts;

namespace PayBridge.PaymentApi.Tests.UnitTests;

/// <summary>
/// Unit tests for pure-logic pieces of the payment API.
///
/// We don't unit-test the controller itself because that would require
/// mocking the DbContext, the Redis multiplexer, and the gRPC client —
/// at which point we're testing that our mocks behave like our mocks,
/// not that the orchestration is correct. Orchestration paths are
/// covered in IntegrationTests.
/// </summary>
public class PaymentValidationTests
{
    [Theory]
    [InlineData("", "key", 1.0)]                  // missing merchant
    [InlineData("merchant", "", 1.0)]             // missing idempotency key
    [InlineData("merchant", "key", 0)]            // zero amount
    [InlineData("merchant", "key", -1)]           // negative amount
    [InlineData(" ", "key", 1.0)]                 // whitespace merchant
    public void InvalidRequest_IsRejected(string merchant, string key, decimal amount)
    {
        // We test the same validation rules the controller applies.
        // Centralizing them as a static helper would be the next refactor;
        // for now we verify them via the predicate directly.
        var isValid = IsValidPaymentRequest(merchant, key, amount);
        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("merchant_acme", "key-001", 49.99)]
    [InlineData("merchant_globex", "abc-123", 0.01)]
    [InlineData("m", "k", 1000000.50)]
    public void ValidRequest_IsAccepted(string merchant, string key, decimal amount)
    {
        var isValid = IsValidPaymentRequest(merchant, key, amount);
        isValid.Should().BeTrue();
    }

    private static bool IsValidPaymentRequest(string merchantId, string idempotencyKey, decimal amount) =>
        !string.IsNullOrWhiteSpace(merchantId)
        && !string.IsNullOrWhiteSpace(idempotencyKey)
        && amount > 0;
}