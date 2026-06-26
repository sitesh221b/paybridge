using PayBridge.Common.Contracts;

namespace PayBridge.SettlementConsumer.Data;

public class SettlementRecord
{
    public long Id { get; set; }
    public Guid PaymentId { get; set; }
    public string MerchantId { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = default!;
    public PaymentStatus FinalStatus { get; set; }
    public string? ProviderTransactionId { get; set; }
    public DateTime EventTimestamp { get; set; }
    public DateTime PersistedAt { get; set; }
}