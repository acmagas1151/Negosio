using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Platform;

public sealed class StaffInvitationConfiguration : IEntityTypeConfiguration<StaffInvitation>
{
    public void Configure(EntityTypeBuilder<StaffInvitation> builder)
    {
        builder.ToTable("StaffInvitations");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.EmailNormalized).IsRequired().HasMaxLength(256);
        builder.Property(i => i.Role).IsRequired().HasConversion<int>();
        builder.Property(i => i.TokenHash).IsRequired().HasMaxLength(128).IsUnicode(false);
        builder.Property(i => i.ExpiresAtUtc).IsRequired();
        builder.Property(i => i.AcceptedAtUtc);
        builder.Property(i => i.RevokedAtUtc);
        builder.Property(i => i.InvitedByUserId).IsRequired();
        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc).IsRequired();

        builder.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName("IX_StaffInvitations_TokenHash");
        builder.HasIndex(i => i.TenantId).HasDatabaseName("IX_StaffInvitations_TenantId");
        // At most one pending invitation per (tenant, email) — filtered so accepted/revoked rows don't collide.
        builder.HasIndex(i => new { i.TenantId, i.EmailNormalized })
            .IsUnique()
            .HasFilter("[AcceptedAtUtc] IS NULL AND [RevokedAtUtc] IS NULL")
            .HasDatabaseName("UX_StaffInvitations_Tenant_Email_Pending");
    }
}
