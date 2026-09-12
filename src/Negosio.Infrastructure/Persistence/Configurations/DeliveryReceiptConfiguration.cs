using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class DeliveryReceiptConfiguration : IEntityTypeConfiguration<DeliveryReceipt>
{
    public void Configure(EntityTypeBuilder<DeliveryReceipt> b)
    {
        b.ToTable("DeliveryReceipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.BranchId).IsRequired();
        b.Property(x => x.SaleId);
        b.Property(x => x.RelatedSaleNumber).HasMaxLength(30).IsUnicode(false);
        // The default values below are inert for the application (the domain constructor always sets
        // SequenceNumber/Status explicitly on every insert, so EF always sends the real value) — they
        // exist purely so the migration's one-time legacy-row backfill has a value to fall back to
        // without diverging the column's steady-state model from what this migration actually applies.
        b.Property(x => x.SequenceNumber).IsRequired().HasDefaultValue(1);
        b.Property(x => x.ScheduledDeliveryDate).IsRequired();
        b.Property(x => x.Status).IsRequired().HasConversion<int>().HasDefaultValue(DeliveryStatus.Delivered);
        b.Property(x => x.RecipientName).IsRequired().HasMaxLength(120);
        b.Property(x => x.DeliveryAddress).IsRequired().HasMaxLength(300);
        b.Property(x => x.ContactNumber).HasMaxLength(40);
        b.Property(x => x.DeliveryNotes).HasMaxLength(1000);
        b.Property(x => x.PreparedByUserId).IsRequired();
        b.Property(x => x.PreparedByNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.DeliveredAtUtc);
        b.Property(x => x.DeliveredByUserId);
        b.Property(x => x.CancelledAtUtc);
        b.Property(x => x.CancelledByUserId);
        b.Property(x => x.CancellationReason).HasMaxLength(500);
        b.Property(x => x.BatchRequestId);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.DeliveryReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Sale-scoped "Delivery 1"/"Delivery 2" label — unique per sale, never a global DR number.
        // SaleId is nullable, so SQL Server treats each NULL as distinct — a delivery with no linked
        // sale never collides with another for this uniqueness check.
        b.HasIndex(x => new { x.SaleId, x.SequenceNumber })
            .IsUnique().HasDatabaseName("IX_DeliveryReceipts_SaleId_SequenceNumber");

        // Retained for "list every delivery for this sale" queries — now genuinely multi-row per sale.
        b.HasIndex(x => new { x.TenantId, x.SaleId })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_SaleId");

        // Batch-create idempotency lookup: a retry with the same BatchRequestId must find (not
        // duplicate) the batch's own rows. Deliberately NOT unique — one batch legitimately writes N
        // rows that all share its BatchRequestId, so a unique index here cannot express this at all
        // (it permits at most one row per key value). Idempotency is instead enforced in
        // DeliveryReceiptService.CreateBatchAsync by re-checking for an existing batch inside the
        // transaction, after LockSaleItemsAsync's pessimistic lock has serialized concurrent attempts
        // against the same sale — a stronger guarantee than a unique index could give here. This index
        // remains purely to make those two lookups fast. Filtered so ad-hoc (non-batch) deliveries,
        // which leave this null, are kept out of it entirely.
        b.HasIndex(x => new { x.TenantId, x.BatchRequestId })
            .HasFilter("[BatchRequestId] IS NOT NULL")
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_BatchRequestId");

        // Report/dashboard access patterns: newest-first within tenant+branch, and by schedule date.
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.CreatedAtUtc })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_BranchId_CreatedAtUtc");
        b.HasIndex(x => new { x.TenantId, x.Status, x.ScheduledDeliveryDate })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate");
    }
}

public sealed class DeliveryReceiptItemConfiguration : IEntityTypeConfiguration<DeliveryReceiptItem>
{
    public void Configure(EntityTypeBuilder<DeliveryReceiptItem> b)
    {
        b.ToTable("DeliveryReceiptItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.DeliveryReceiptId).IsRequired();
        b.Property(x => x.SaleItemId).IsRequired();
        b.Property(x => x.ProductNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.VariantNameSnapshot).HasMaxLength(200);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.UnitPrice).HasPrecision(18, 2);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<SaleItem>().WithMany().HasForeignKey(x => x.SaleItemId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.DeliveryReceiptId })
            .HasDatabaseName("IX_DeliveryReceiptItems_TenantId_DeliveryReceiptId");

        // A sale item cannot appear twice within the same delivery.
        b.HasIndex(x => new { x.DeliveryReceiptId, x.SaleItemId })
            .IsUnique().HasDatabaseName("IX_DeliveryReceiptItems_DeliveryReceiptId_SaleItemId");

        // Fulfillment allocation (Pending/Delivered/Unscheduled per sale item) is computed by joining
        // this column back to its sale item's assignments across every non-cancelled delivery.
        b.HasIndex(x => new { x.TenantId, x.SaleItemId })
            .HasDatabaseName("IX_DeliveryReceiptItems_TenantId_SaleItemId");
    }
}
