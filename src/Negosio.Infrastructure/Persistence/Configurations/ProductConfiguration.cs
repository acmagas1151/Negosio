using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.CategoryId).IsRequired();
        builder.Property(p => p.Name).IsRequired().HasMaxLength(150);
        builder.Property(p => p.Description).HasMaxLength(1000);
        builder.Property(p => p.TrackInventory).IsRequired();
        builder.Property(p => p.IsActive).IsRequired();
        builder.Property(p => p.HasVariants).IsRequired();
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsRequired();

        // A product references a category; categories are deactivated, never deleted while referenced.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Product owns its variants (field-backed collection, like Tenant.Branches).
        builder.HasMany(p => p.Variants)
            .WithOne()
            .HasForeignKey(v => v.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(p => new { p.TenantId, p.CategoryId }).HasDatabaseName("IX_Products_TenantId_CategoryId");
        builder.HasIndex(p => new { p.TenantId, p.Name }).HasDatabaseName("IX_Products_TenantId_Name");
        builder.HasIndex(p => new { p.TenantId, p.IsActive }).HasDatabaseName("IX_Products_TenantId_IsActive");
    }
}
