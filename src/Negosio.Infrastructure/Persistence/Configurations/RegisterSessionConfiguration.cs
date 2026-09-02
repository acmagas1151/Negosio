using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RegisterSessionConfiguration : IEntityTypeConfiguration<RegisterSession>
{
    public void Configure(EntityTypeBuilder<RegisterSession> builder)
    {
        builder.ToTable("RegisterSessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.BranchId).IsRequired();
        builder.Property(s => s.RegisterId).IsRequired();
        builder.Property(s => s.OpenedByUserId).IsRequired();
        builder.Property(s => s.OpenedAtUtc).IsRequired();
        builder.Property(s => s.OpeningCash).HasPrecision(18, 2);
        builder.Property(s => s.ClosingCash).HasPrecision(18, 2);
        builder.Property(s => s.ExpectedCash).HasPrecision(18, 2);
        builder.Property(s => s.CashDifference).HasPrecision(18, 2);
        builder.Property(s => s.GrossCashSales).HasPrecision(18, 2);
        builder.Property(s => s.VoidedCashSales).HasPrecision(18, 2);
        builder.Property(s => s.RefundCashOut).HasPrecision(18, 2);
        builder.Property(s => s.CashIn).HasPrecision(18, 2);
        builder.Property(s => s.CashOut).HasPrecision(18, 2);
        builder.Property(s => s.Status).IsRequired().HasConversion<int>();
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsRequired();

        builder.HasOne<Register>()
            .WithMany()
            .HasForeignKey(s => s.RegisterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // At most one OPEN session per register.
        builder.HasIndex(s => s.RegisterId)
            .IsUnique()
            .HasFilter($"[Status] = {(int)RegisterSessionStatus.Open}")
            .HasDatabaseName("IX_RegisterSessions_RegisterId_Open");

        // At most one OPEN session per user (a cashier can't hold two tills at once).
        builder.HasIndex(s => new { s.TenantId, s.OpenedByUserId })
            .IsUnique()
            .HasFilter($"[Status] = {(int)RegisterSessionStatus.Open}")
            .HasDatabaseName("IX_RegisterSessions_OpenedByUserId_Open");

        builder.HasIndex(s => new { s.TenantId, s.RegisterId, s.Status })
            .HasDatabaseName("IX_RegisterSessions_TenantId_RegisterId_Status");
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.OpenedAtUtc })
            .HasDatabaseName("IX_RegisterSessions_TenantId_BranchId_OpenedAtUtc");
    }
}
