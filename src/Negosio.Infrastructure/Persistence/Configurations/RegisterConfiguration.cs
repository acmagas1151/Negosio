using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RegisterConfiguration : IEntityTypeConfiguration<Register>
{
    public void Configure(EntityTypeBuilder<Register> builder)
    {
        builder.ToTable("Registers");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.BranchId).IsRequired();
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Code).IsRequired().HasMaxLength(20).IsUnicode(false);
        builder.Property(r => r.IsActive).IsRequired();
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(r => r.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // A register code is unique within a branch.
        builder.HasIndex(r => new { r.TenantId, r.BranchId, r.Code })
            .IsUnique()
            .HasDatabaseName("IX_Registers_TenantId_BranchId_Code");
    }
}
