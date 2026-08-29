using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.BranchId).IsRequired();
        builder.Property(m => m.ProductVariantId).IsRequired();
        builder.Property(m => m.Type).IsRequired().HasConversion<int>();
        builder.Property(m => m.Quantity).HasPrecision(18, 3);
        builder.Property(m => m.QuantityBefore).HasPrecision(18, 3);
        builder.Property(m => m.QuantityAfter).HasPrecision(18, 3);
        builder.Property(m => m.ReferenceType).HasMaxLength(100);
        builder.Property(m => m.Reason).HasMaxLength(500);
        builder.Property(m => m.CreatedByUserId).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc).IsRequired();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(m => m.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(m => m.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Paged, tenant-scoped, newest-first movement history filtered by branch or by sellable item.
        builder.HasIndex(m => new { m.TenantId, m.BranchId, m.CreatedAtUtc })
            .HasDatabaseName("IX_StockMovements_TenantId_BranchId_CreatedAtUtc");
        builder.HasIndex(m => new { m.TenantId, m.ProductVariantId, m.CreatedAtUtc })
            .HasDatabaseName("IX_StockMovements_TenantId_ProductVariantId_CreatedAtUtc");
    }
}
