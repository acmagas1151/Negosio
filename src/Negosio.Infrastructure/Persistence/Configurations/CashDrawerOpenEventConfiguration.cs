using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class CashDrawerOpenEventConfiguration : IEntityTypeConfiguration<CashDrawerOpenEvent>
{
    public void Configure(EntityTypeBuilder<CashDrawerOpenEvent> builder)
    {
        builder.ToTable("CashDrawerOpenEvents");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.BranchId).IsRequired();
        builder.Property(e => e.RegisterSessionId).IsRequired();
        builder.Property(e => e.RequestedByUserId).IsRequired();
        builder.Property(e => e.ApprovedByUserId);
        builder.Property(e => e.CreatedAtUtc).IsRequired();
        builder.Property(e => e.UpdatedAtUtc).IsRequired();

        builder.HasOne<RegisterSession>().WithMany().HasForeignKey(e => e.RegisterSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TenantId, e.RegisterSessionId, e.CreatedAtUtc })
            .HasDatabaseName("IX_CashDrawerOpenEvents_TenantId_RegisterSessionId_CreatedAtUtc");
    }
}
