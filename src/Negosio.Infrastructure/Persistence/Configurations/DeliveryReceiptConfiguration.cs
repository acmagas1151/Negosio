using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

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
        b.Property(x => x.RecipientName).IsRequired().HasMaxLength(120);
        b.Property(x => x.DeliveryAddress).IsRequired().HasMaxLength(300);
        b.Property(x => x.ContactNumber).HasMaxLength(40);
        b.Property(x => x.DeliveryNotes).HasMaxLength(1000);
        b.Property(x => x.PreparedByUserId).IsRequired();
        b.Property(x => x.PreparedByNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.DeliveryReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // NON-unique — one-per-sale is a service rule, not a schema rule (multiple deliveries later)
        b.HasIndex(x => new { x.TenantId, x.SaleId })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_SaleId");
        // (No Number column and no Number index — DR numbering removed for v1.)
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
        b.Property(x => x.ProductNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.VariantNameSnapshot).HasMaxLength(200);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.UnitPrice).HasPrecision(18, 2);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.DeliveryReceiptId })
            .HasDatabaseName("IX_DeliveryReceiptItems_TenantId_DeliveryReceiptId");
    }
}
