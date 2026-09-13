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
        // Existing rows are all deliveries; the default is what the migration backfills them with.
        b.Property(x => x.Method).IsRequired().HasConversion<int>().HasDefaultValue(FulfillmentMethod.Delivery);
        b.Property(x => x.ScheduledDate).IsRequired();
        b.Property(x => x.Status).IsRequired().HasConversion<int>().HasDefaultValue(FulfillmentStatus.Completed);
        b.Property(x => x.RecipientName).IsRequired().HasMaxLength(120);
        // Nullable now: a pickup transports nothing, so it has no address. Required-for-delivery is a
        // domain rule (DeliveryReceipt.CreateDelivery), deliberately not a schema rule — the column has to
        // permit null for pickup rows.
        b.Property(x => x.DeliveryAddress).HasMaxLength(300);
        b.Property(x => x.ContactNumber).HasMaxLength(40);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.PreparedByUserId).IsRequired();
        b.Property(x => x.PreparedByNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.CompletedAtUtc);
        b.Property(x => x.CompletedByUserId);
        b.Property(x => x.CancelledAtUtc);
        b.Property(x => x.CancelledByUserId);
        b.Property(x => x.CancellationReason).HasMaxLength(500);
        b.Property(x => x.CancellationDisposition).HasConversion<int>();
        b.Property(x => x.BatchRequestId);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.DeliveryReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Per-sale, per-method label. SaleId is nullable and EF's SQL Server provider adds
        // `WHERE [SaleId] IS NOT NULL` for a nullable unique index, so a sale-less schedule never
        // collides here.
        b.HasIndex(x => new { x.SaleId, x.Method, x.SequenceNumber })
            .IsUnique().HasDatabaseName("IX_DeliveryReceipts_SaleId_Method_SequenceNumber");

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
        b.HasIndex(x => new { x.TenantId, x.Method, x.Status, x.ScheduledDate })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_Method_Status_ScheduledDate");
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

        // Fulfillment allocation (Pending/Completed/Unscheduled per sale item) is computed by joining
        // this column back to its sale item's assignments across every non-cancelled delivery.
        b.HasIndex(x => new { x.TenantId, x.SaleItemId })
            .HasDatabaseName("IX_DeliveryReceiptItems_TenantId_SaleItemId");
    }
}
