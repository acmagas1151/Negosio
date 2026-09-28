using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoTableConfiguration : IEntityTypeConfiguration<RestoTable>
{
    public void Configure(EntityTypeBuilder<RestoTable> builder)
    {
        builder.ToTable("RestoTables");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.BranchId).IsRequired();
        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.Property(t => t.IsActive).IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.BranchId })
            .HasDatabaseName("IX_RestoTables_TenantId_BranchId");
    }
}
