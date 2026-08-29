using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.TenantId).IsRequired();
        builder.Property(b => b.Name).IsRequired().HasMaxLength(150);
        builder.Property(b => b.Code).IsRequired().HasMaxLength(20);
        builder.Property(b => b.AddressLine1).IsRequired().HasMaxLength(200);
        builder.Property(b => b.AddressLine2).HasMaxLength(200);
        builder.Property(b => b.City).IsRequired().HasMaxLength(100);
        builder.Property(b => b.Province).IsRequired().HasMaxLength(100);
        builder.Property(b => b.PostalCode).HasMaxLength(20);
        builder.Property(b => b.IsActive).IsRequired();
        builder.Property(b => b.CreatedAtUtc).IsRequired();
        builder.Property(b => b.UpdatedAtUtc).IsRequired();

        // Branch code is unique within a tenant.
        builder.HasIndex(b => new { b.TenantId, b.Code })
            .IsUnique()
            .HasDatabaseName("IX_Branches_TenantId_Code");
    }
}
