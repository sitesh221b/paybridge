using Microsoft.EntityFrameworkCore;

namespace PayBridge.PaymentApi.Data;

public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Payment>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.MerchantId).HasMaxLength(64).IsRequired();
            e.Property(p => p.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(p => p.Currency).HasMaxLength(3).IsRequired();
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.Method).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.ProviderTransactionId).HasMaxLength(64);
            e.Property(p => p.OriginalTraceId).HasMaxLength(64);
            e.Property(p => p.OriginalSpanId).HasMaxLength(64);

            // THE idempotency guarantee: durable, enforced by Postgres.
            // Cache can be lost; this constraint cannot be bypassed.
            e.HasIndex(p => new { p.MerchantId, p.IdempotencyKey })
             .IsUnique()
             .HasDatabaseName("ux_payments_merchant_idempotency");
        });
    }
}