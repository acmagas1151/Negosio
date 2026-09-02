using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class UserPermissionGrantConfiguration : IEntityTypeConfiguration<UserPermissionGrant>
{
    public void Configure(EntityTypeBuilder<UserPermissionGrant> builder)
    {
        builder.ToTable("UserPermissionGrants");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.TenantId).IsRequired();
        builder.Property(g => g.UserId).IsRequired();
        builder.Property(g => g.Permission).IsRequired().HasConversion<int>();
        builder.Property(g => g.GrantedByUserId).IsRequired();
        builder.Property(g => g.CreatedAtUtc).IsRequired();
        builder.Property(g => g.UpdatedAtUtc).IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => new { g.TenantId, g.UserId, g.Permission })
            .IsUnique().HasDatabaseName("IX_UserPermissionGrants_TenantId_UserId_Permission");
    }
}
