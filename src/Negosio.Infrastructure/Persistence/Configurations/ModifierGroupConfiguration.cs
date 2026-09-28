using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ModifierGroupConfiguration : IEntityTypeConfiguration<ModifierGroup>
{
    public void Configure(EntityTypeBuilder<ModifierGroup> builder)
    {
        builder.ToTable("ModifierGroups");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.TenantId).IsRequired();
        builder.Property(g => g.Name).IsRequired().HasMaxLength(100);
        builder.Property(g => g.SelectionType).IsRequired().HasConversion<int>();
        builder.Property(g => g.IsActive).IsRequired();
        builder.Property(g => g.CreatedAtUtc).IsRequired();
        builder.Property(g => g.UpdatedAtUtc).IsRequired();

        builder.HasMany(g => g.Options)
            .WithOne()
            .HasForeignKey(o => o.ModifierGroupId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(g => g.Options).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(g => g.TenantId).HasDatabaseName("IX_ModifierGroups_TenantId");
    }
}

public sealed class ModifierOptionConfiguration : IEntityTypeConfiguration<ModifierOption>
{
    public void Configure(EntityTypeBuilder<ModifierOption> builder)
    {
        builder.ToTable("ModifierOptions");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.ModifierGroupId).IsRequired();
        builder.Property(o => o.Name).IsRequired().HasMaxLength(100);
        builder.Property(o => o.PriceDelta).HasPrecision(18, 2);
        builder.Property(o => o.IsActive).IsRequired();
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();

        builder.HasIndex(o => new { o.TenantId, o.ModifierGroupId })
            .HasDatabaseName("IX_ModifierOptions_TenantId_ModifierGroupId");
    }
}
