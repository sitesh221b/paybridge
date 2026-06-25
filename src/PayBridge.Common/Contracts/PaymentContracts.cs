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

public record FraudCheckRequest(
    Guid PaymentId,
    string MerchantId,
    decimal Amount,
    string Currency,
    string CustomerEmail,
    PaymentMethod Method
);

public record FraudCheckResponse(
    bool Approved,
    double RiskScore,
    string Reason
);