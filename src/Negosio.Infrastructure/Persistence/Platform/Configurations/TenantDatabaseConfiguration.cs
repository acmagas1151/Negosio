using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Platform;

public sealed class TenantDatabaseConfiguration : IEntityTypeConfiguration<TenantDatabase>
{
    public void Configure(EntityTypeBuilder<TenantDatabase> builder)
    {
        builder.ToTable("TenantDatabases");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.DatabaseName).IsRequired().HasMaxLength(128).IsUnicode(false);
        builder.Property(d => d.ServerKey).IsRequired().HasMaxLength(64).IsUnicode(false);
        builder.Property(d => d.Status).IsRequired().HasConversion<int>();
        builder.Property(d => d.CreatedAtUtc).IsRequired();
        builder.Property(d => d.UpdatedAtUtc).IsRequired();

        builder.HasOne<Tenant>()
            .WithOne()
            .HasForeignKey<TenantDatabase>(d => d.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => d.TenantId).IsUnique().HasDatabaseName("IX_TenantDatabases_TenantId");
        builder.HasIndex(d => d.DatabaseName).IsUnique().HasDatabaseName("IX_TenantDatabases_DatabaseName");
    }
}
