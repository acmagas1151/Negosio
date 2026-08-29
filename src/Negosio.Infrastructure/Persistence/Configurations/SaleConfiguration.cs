using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("Sales");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.BranchId).IsRequired();
        builder.Property(s => s.RegisterSessionId).IsRequired();
        builder.Property(s => s.SaleNumber).IsRequired().HasMaxLength(30).IsUnicode(false);
        builder.Property(s => s.ClientRequestId).IsRequired();
        builder.Property(s => s.Status).IsRequired().HasConversion<int>();
        builder.Property(s => s.Subtotal).HasPrecision(18, 2);
        builder.Property(s => s.DiscountTotal).HasPrecision(18, 2);
        builder.Property(s => s.TaxTotal).HasPrecision(18, 2);
        builder.Property(s => s.GrandTotal).HasPrecision(18, 2);
        builder.Property(s => s.AmountPaid).HasPrecision(18, 2);
        builder.Property(s => s.ChangeDue).HasPrecision(18, 2);
        builder.Property(s => s.CreatedByUserId).IsRequired();
        builder.Property(s => s.VoidReason).HasMaxLength(500);
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsRequired();

        builder.HasOne<RegisterSession>()
            .WithMany()
            .HasForeignKey(s => s.RegisterSessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(s => s.Items)
            .WithOne()
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(s => s.Payments)
            .WithOne()
            .HasForeignKey(p => p.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Receipt number + idempotency key, both unique per tenant.
        builder.HasIndex(s => new { s.TenantId, s.SaleNumber })
            .IsUnique().HasDatabaseName("IX_Sales_TenantId_SaleNumber");
        builder.HasIndex(s => new { s.TenantId, s.ClientRequestId })
            .IsUnique().HasDatabaseName("IX_Sales_TenantId_ClientRequestId");

        // Sales-history filters, each newest-first within the leading predicate.
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.CreatedAtUtc })
            .HasDatabaseName("IX_Sales_TenantId_BranchId_CreatedAtUtc");
        builder.HasIndex(s => new { s.TenantId, s.RegisterSessionId, s.CreatedAtUtc })
            .HasDatabaseName("IX_Sales_TenantId_RegisterSessionId_CreatedAtUtc");
        builder.HasIndex(s => new { s.TenantId, s.CreatedByUserId, s.CreatedAtUtc })
            .HasDatabaseName("IX_Sales_TenantId_CreatedByUserId_CreatedAtUtc");
        builder.HasIndex(s => new { s.TenantId, s.Status, s.CreatedAtUtc })
            .HasDatabaseName("IX_Sales_TenantId_Status_CreatedAtUtc");
    }
}
