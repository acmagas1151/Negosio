using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.SaleId).IsRequired();
        builder.Property(p => p.Method).IsRequired().HasConversion<int>();
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.ReferenceNumber).HasMaxLength(100);
        builder.Property(p => p.ReceivedAmount).HasPrecision(18, 2);
        builder.Property(p => p.ChangeAmount).HasPrecision(18, 2);
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.SaleId }).HasDatabaseName("IX_Payments_TenantId_SaleId");
    }
}
