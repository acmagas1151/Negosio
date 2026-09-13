using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class FulfillmentConversionConfiguration : IEntityTypeConfiguration<FulfillmentConversion>
{
    public void Configure(EntityTypeBuilder<FulfillmentConversion> b)
    {
        b.ToTable("FulfillmentConversions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.SaleId).IsRequired();
        b.Property(x => x.SaleItemId).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.FromMethod).IsRequired().HasConversion<int>();
        b.Property(x => x.ToMethod).IsRequired().HasConversion<int>();
        b.Property(x => x.SourceRecordId);
        b.Property(x => x.ReplacementRecordId);
        b.Property(x => x.Reason).IsRequired().HasMaxLength(500);
        b.Property(x => x.CreatedByUserId).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SaleItem>().WithMany().HasForeignKey(x => x.SaleItemId).OnDelete(DeleteBehavior.Restrict);

        // Sale-detail's conversion history and the combined report both read by sale, newest first.
        b.HasIndex(x => new { x.TenantId, x.SaleId, x.CreatedAtUtc })
            .HasDatabaseName("IX_FulfillmentConversions_TenantId_SaleId_CreatedAtUtc");
        // Per-line audit replay (reconstructing the original checkout allocation).
        b.HasIndex(x => new { x.TenantId, x.SaleItemId })
            .HasDatabaseName("IX_FulfillmentConversions_TenantId_SaleItemId");
        // Retry-safety: one conversion row per (source schedule, sale item) — a retried cancellation
        // that re-runs the same conversion collides here instead of double-counting the quantity.
        // Filtered because a release with no source record leaves SourceRecordId null.
        b.HasIndex(x => new { x.SourceRecordId, x.SaleItemId })
            .IsUnique()
            .HasFilter("[SourceRecordId] IS NOT NULL")
            .HasDatabaseName("IX_FulfillmentConversions_SourceRecordId_SaleItemId");
    }
}
