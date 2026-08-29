using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RefundPaymentConfiguration : IEntityTypeConfiguration<RefundPayment>
{
    public void Configure(EntityTypeBuilder<RefundPayment> builder)
    {
        builder.ToTable("RefundPayments");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.SaleReturnId).IsRequired();
        builder.Property(r => r.Method).IsRequired().HasConversion<int>();
        builder.Property(r => r.Amount).HasPrecision(18, 2);
        builder.Property(r => r.ReferenceNumber).HasMaxLength(100);
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.SaleReturnId }).HasDatabaseName("IX_RefundPayments_TenantId_SaleReturnId");
    }
}
