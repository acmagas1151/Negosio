using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderConfiguration : IEntityTypeConfiguration<RestoOrder>
{
    public void Configure(EntityTypeBuilder<RestoOrder> builder)
    {
        builder.ToTable("RestoOrders");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.BranchId).IsRequired();
        builder.Property(o => o.RegisterSessionId).IsRequired();
        builder.Property(o => o.ServiceType).IsRequired().HasConversion<int>();
        builder.Property(o => o.DisplayLabel).HasMaxLength(100);
        builder.Property(o => o.Status).IsRequired().HasConversion<int>();
        builder.Property(o => o.OpenedByUserId).IsRequired();
        builder.Property(o => o.OpenedAtUtc).IsRequired();
        builder.Property(o => o.CancelReason).HasMaxLength(500);
        builder.Property(o => o.RowVersion).IsRowVersion();
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();

        builder.HasOne<RestoTable>()
            .WithMany()
            .HasForeignKey(o => o.TableId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RegisterSession>()
            .WithMany()
            .HasForeignKey(o => o.RegisterSessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(o => o.Rounds)
            .WithOne()
            .HasForeignKey(r => r.RestoOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Rounds).UsePropertyAccessMode(PropertyAccessMode.Field);

        // At most one Open RestoOrder per table — mirrors IX_RegisterSessions_RegisterId_Open exactly.
        // This is the constraint that actually prevents two concurrent visits on the same table.
        builder.HasIndex(o => o.TableId)
            .IsUnique()
            .HasFilter($"[Status] = {(int)RestoOrderStatus.Open} AND [TableId] IS NOT NULL")
            .HasDatabaseName("IX_RestoOrders_TableId_Open");

        builder.HasIndex(o => new { o.TenantId, o.BranchId, o.Status })
            .HasDatabaseName("IX_RestoOrders_TenantId_BranchId_Status");
    }
}
