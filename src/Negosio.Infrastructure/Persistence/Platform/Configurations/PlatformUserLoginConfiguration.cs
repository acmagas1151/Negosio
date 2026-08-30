using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Platform;

public sealed class PlatformUserLoginConfiguration : IEntityTypeConfiguration<PlatformUserLogin>
{
    public void Configure(EntityTypeBuilder<PlatformUserLogin> builder)
    {
        builder.ToTable("PlatformUserLogins");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();

        builder.Property(l => l.TenantId).IsRequired();
        builder.Property(l => l.EmailNormalized).IsRequired().HasMaxLength(256);
        builder.Property(l => l.PasswordHash).IsRequired().HasMaxLength(500);
        builder.Property(l => l.Role).IsRequired().HasConversion<int>();
        builder.Property(l => l.IsActive).IsRequired();
        builder.Property(l => l.CreatedAtUtc).IsRequired();
        builder.Property(l => l.UpdatedAtUtc).IsRequired();

        // One email = one login = one tenant (preserves Phase 1's global-unique-email behaviour).
        builder.HasIndex(l => l.EmailNormalized).IsUnique().HasDatabaseName("IX_PlatformUserLogins_EmailNormalized");
        builder.HasIndex(l => l.TenantId).HasDatabaseName("IX_PlatformUserLogins_TenantId");
    }
}
