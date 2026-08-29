using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class SaleReturnItemConfiguration : IEntityTypeConfiguration<SaleReturnItem>
{
    public void Configure(EntityTypeBuilder<SaleReturnItem> builder)
    {
        builder.ToTable("SaleReturnItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.SaleReturnId).IsRequired();
        builder.Property(i => i.SaleItemId).IsRequired();
        builder.Property(i => i.ProductVariantId).IsRequired();
        builder.Property(i => i.ProductNameSnapshot).IsRequired().HasMaxLength(150);
        builder.Property(i => i.Quantity).HasPrecision(18, 3);
        builder.Property(i => i.RefundAmount).HasPrecision(18, 2);
        builder.Property(i => i.Restocked).IsRequired();
        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc).IsRequired();

        builder.HasOne<SaleItem>()
            .WithMany()
            .HasForeignKey(i => i.SaleItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.TenantId, i.SaleReturnId }).HasDatabaseName("IX_SaleReturnItems_TenantId_SaleReturnId");
    }
}
