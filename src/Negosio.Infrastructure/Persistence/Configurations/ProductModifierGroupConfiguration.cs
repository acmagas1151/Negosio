using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ProductModifierGroupConfiguration : IEntityTypeConfiguration<ProductModifierGroup>
{
    public void Configure(EntityTypeBuilder<ProductModifierGroup> builder)
    {
        builder.ToTable("ProductModifierGroups");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.ModifierGroupId).IsRequired();
        builder.Property(x => x.IsRequired).IsRequired();
        builder.Property(x => x.DisplayOrder).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ModifierGroup>()
            .WithMany()
            .HasForeignKey(x => x.ModifierGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // One product offers a given modifier group at most once.
        builder.HasIndex(x => new { x.TenantId, x.ProductId, x.ModifierGroupId })
            .IsUnique().HasDatabaseName("IX_ProductModifierGroups_TenantId_ProductId_ModifierGroupId");
    }
}
