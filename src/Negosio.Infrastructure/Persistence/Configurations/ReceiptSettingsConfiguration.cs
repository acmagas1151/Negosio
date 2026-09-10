using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ReceiptSettingsConfiguration : IEntityTypeConfiguration<ReceiptSettings>
{
    public void Configure(EntityTypeBuilder<ReceiptSettings> b)
    {
        b.ToTable("ReceiptSettings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.BranchId);
        b.Property(x => x.Width).IsRequired().HasConversion<int>();

        b.Property(x => x.SalesHeaderText).HasMaxLength(500);
        b.Property(x => x.SalesFooterText).HasMaxLength(500);
        b.Property(x => x.DeliveryHeaderText).HasMaxLength(500);
        b.Property(x => x.DeliveryFooterText).HasMaxLength(500);

        b.Property(x => x.SalesShowBranch).IsRequired();
        b.Property(x => x.SalesShowCashier).IsRequired();
        b.Property(x => x.SalesShowPaymentMethod).IsRequired();
        b.Property(x => x.SalesShowTaxLine).IsRequired();
        b.Property(x => x.SalesShowReferenceNumber).IsRequired();
        b.Property(x => x.DeliveryShowPrices).IsRequired();
        b.Property(x => x.DeliveryShowRelatedSaleNumber).IsRequired();
        b.Property(x => x.DeliveryShowContactNumber).IsRequired();
        b.Property(x => x.DeliveryShowSignatureFields).IsRequired();

        b.Property(x => x.UpdatedByUserId).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        // exactly one tenant-default row
        b.HasIndex(x => x.TenantId)
            .IsUnique()
            .HasFilter("[BranchId] IS NULL")
            .HasDatabaseName("UX_ReceiptSettings_TenantDefault");
        // one row per branch
        b.HasIndex(x => new { x.TenantId, x.BranchId })
            .IsUnique()
            .HasFilter("[BranchId] IS NOT NULL")
            .HasDatabaseName("UX_ReceiptSettings_TenantId_BranchId");
    }
}
