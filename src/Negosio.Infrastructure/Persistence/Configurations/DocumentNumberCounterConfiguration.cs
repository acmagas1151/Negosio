using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class DocumentNumberCounterConfiguration : IEntityTypeConfiguration<DocumentNumberCounter>
{
    public void Configure(EntityTypeBuilder<DocumentNumberCounter> builder)
    {
        builder.ToTable("DocumentNumberCounters");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.BranchId).IsRequired();
        builder.Property(c => c.Type).IsRequired().HasConversion<int>();
        builder.Property(c => c.LastNumber).IsRequired();

        // One counter row per (tenant, branch, document type).
        builder.HasIndex(c => new { c.TenantId, c.BranchId, c.Type })
            .IsUnique()
            .HasDatabaseName("IX_DocumentNumberCounters_TenantId_BranchId_Type");
    }
}
