using Microsoft.EntityFrameworkCore;

namespace PayBridge.SettlementConsumer.Data;

public class SettlementDbContext : DbContext
{
    public SettlementDbContext(DbContextOptions<SettlementDbContext> options) : base(options) { }

    public DbSet<SettlementRecord> Settlements => Set<SettlementRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SettlementRecord>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.MerchantId).HasMaxLength(64).IsRequired();
            e.Property(s => s.TenantId).HasMaxLength(64).IsRequired();
            e.Property(s => s.Currency).HasMaxLength(3).IsRequired();
            e.Property(s => s.Amount).HasPrecision(18, 2);
            e.Property(s => s.FinalStatus).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.ProviderTransactionId).HasMaxLength(64);

            // Idempotent persistence: same PaymentId can never be persisted twice.
            // Critical because at-least-once delivery means duplicate messages
            // are expected.
            e.HasIndex(s => s.PaymentId)
                .IsUnique()
                .HasDatabaseName("ux_settlements_payment_id");
        });
    }
}