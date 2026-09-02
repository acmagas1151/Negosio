using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RegisterCashMovementConfiguration : IEntityTypeConfiguration<RegisterCashMovement>
{
    public void Configure(EntityTypeBuilder<RegisterCashMovement> builder)
    {
        builder.ToTable("RegisterCashMovements");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.BranchId).IsRequired();
        builder.Property(m => m.RegisterSessionId).IsRequired();
        builder.Property(m => m.Type).IsRequired().HasConversion<int>();
        builder.Property(m => m.Amount).HasPrecision(18, 2);
        builder.Property(m => m.Reason).IsRequired().HasMaxLength(500);
        builder.Property(m => m.CreatedByUserId).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc).IsRequired();

        builder.HasOne<RegisterSession>().WithMany().HasForeignKey(m => m.RegisterSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.TenantId, m.RegisterSessionId, m.CreatedAtUtc })
            .HasDatabaseName("IX_RegisterCashMovements_TenantId_RegisterSessionId_CreatedAtUtc");
    }
}
