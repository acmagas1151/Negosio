using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderRoundConfiguration : IEntityTypeConfiguration<RestoOrderRound>
{
    public void Configure(EntityTypeBuilder<RestoOrderRound> builder)
    {
        builder.ToTable("RestoOrderRounds");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.RestoOrderId).IsRequired();
        builder.Property(r => r.RoundNumber).IsRequired();
        builder.Property(r => r.Status).IsRequired().HasConversion<int>();
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        builder.HasMany(r => r.Items)
            .WithOne()
            .HasForeignKey(i => i.RestoOrderRoundId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => new { r.TenantId, r.RestoOrderId })
            .HasDatabaseName("IX_RestoOrderRounds_TenantId_RestoOrderId");

        // Backstops the in-memory RestoOrder.OpenNextRound() sequential-numbering logic — the same
        // discipline as RegisterSession's filtered unique indexes.
        builder.HasIndex(r => new { r.TenantId, r.RestoOrderId, r.RoundNumber })
            .IsUnique()
            .HasDatabaseName("IX_RestoOrderRounds_TenantId_RestoOrderId_RoundNumber");
    }
}
