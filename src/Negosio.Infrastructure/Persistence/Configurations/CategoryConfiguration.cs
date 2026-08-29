using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.Name).IsRequired().HasMaxLength(150);
        builder.Property(c => c.NormalizedName).IsRequired().HasMaxLength(150);
        builder.Property(c => c.Description).HasMaxLength(1000);
        builder.Property(c => c.IsActive).IsRequired();
        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc).IsRequired();

        // Enforces case/whitespace-insensitive uniqueness of category names within a tenant, and
        // serves tenant-scoped name lookups from GET /api/categories?search=.
        builder.HasIndex(c => new { c.TenantId, c.NormalizedName })
            .IsUnique()
            .HasDatabaseName("IX_Categories_TenantId_NormalizedName");
    }
}
