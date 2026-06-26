namespace PayBridge.Common.Contracts;

public enum PaymentMethod { CreditCard, DebitCard, BankTransfer, Wallet }

public enum PaymentStatus { Created, FraudChecking, Submitted, Completed, Failed, Refunded }

public record CreatePaymentRequest(
    string MerchantId,
    string IdempotencyKey,
    decimal Amount,
    string Currency,
    string CustomerEmail,
    PaymentMethod Method,
    Dictionary<string, string>? Metadata
);

public record CreatePaymentResponse(
    Guid PaymentId,
    PaymentStatus Status,
    DateTime CreatedAt
);

public record SubmitToProviderRequest(
    Guid PaymentId,
    decimal Amount,
    string Currency,
    PaymentMethod Method,
    string WebhookUrl
);

public record SubmitToProviderResponse(
    string ProviderTransactionId,
    string Status  // "ACCEPTED" — final status comes via webhook
);

public record ProviderWebhookCallback(
    string ProviderTransactionId,
    string Status,  // "SUCCESS" | "FAILED"
    DateTime Timestamp,
    Dictionary<string, string> Metadata
);