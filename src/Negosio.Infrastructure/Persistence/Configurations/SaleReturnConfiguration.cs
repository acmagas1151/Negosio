using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class SaleReturnConfiguration : IEntityTypeConfiguration<SaleReturn>
{
    public void Configure(EntityTypeBuilder<SaleReturn> builder)
    {
        builder.ToTable("SaleReturns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.SaleId).IsRequired();
        builder.Property(r => r.BranchId).IsRequired();
        builder.Property(r => r.ReturnNumber).IsRequired().HasMaxLength(30).IsUnicode(false);
        builder.Property(r => r.CreatedByUserId).IsRequired();
        builder.Property(r => r.ApprovedByUserId).IsRequired(false);
        builder.Property(r => r.Reason).IsRequired().HasMaxLength(500);
        builder.Property(r => r.TotalRefund).HasPrecision(18, 2);
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        builder.HasOne<Sale>()
            .WithMany()
            .HasForeignKey(r => r.SaleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Items)
            .WithOne()
            .HasForeignKey(i => i.SaleReturnId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(r => r.Refunds)
            .WithOne()
            .HasForeignKey(rp => rp.SaleReturnId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(r => r.Refunds).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => new { r.TenantId, r.SaleId }).HasDatabaseName("IX_SaleReturns_TenantId_SaleId");
        builder.HasIndex(r => new { r.TenantId, r.BranchId, r.CreatedAtUtc })
            .HasDatabaseName("IX_SaleReturns_TenantId_BranchId_CreatedAtUtc");
        builder.HasIndex(r => new { r.TenantId, r.BranchId, r.ReturnNumber })
            .IsUnique().HasDatabaseName("IX_SaleReturns_TenantId_BranchId_ReturnNumber");
    }
}
