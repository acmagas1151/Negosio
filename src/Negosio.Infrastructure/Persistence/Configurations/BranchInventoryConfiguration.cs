using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class BranchInventoryConfiguration : IEntityTypeConfiguration<BranchInventory>
{
    public void Configure(EntityTypeBuilder<BranchInventory> builder)
    {
        builder.ToTable("BranchInventories");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.BranchId).IsRequired();
        builder.Property(i => i.ProductVariantId).IsRequired();
        builder.Property(i => i.QuantityOnHand).HasPrecision(18, 3);
        builder.Property(i => i.ReorderLevel).HasPrecision(18, 3);
        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc).IsRequired();

        // SQL Server rowversion -> EF optimistic-concurrency token.
        builder.Property(i => i.RowVersion).IsRowVersion();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(i => i.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict);

        // One inventory row per sellable item per branch.
        builder.HasIndex(i => new { i.TenantId, i.BranchId, i.ProductVariantId })
            .IsUnique()
            .HasDatabaseName("IX_BranchInventories_TenantId_BranchId_ProductVariantId");

        builder.HasIndex(i => new { i.TenantId, i.BranchId })
            .HasDatabaseName("IX_BranchInventories_TenantId_BranchId");
        builder.HasIndex(i => new { i.TenantId, i.ProductVariantId })
            .HasDatabaseName("IX_BranchInventories_TenantId_ProductVariantId");
    }
}
