using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Domain.Common;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence;

/// <summary>
/// The platform (control-plane) database: tenant identity, database routing and the login directory.
/// One fixed database for the whole platform.
/// </summary>
public sealed class PlatformDbContext : DbContext, IPlatformDbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDatabase> TenantDatabases => Set<TenantDatabase>();

    public DbSet<PlatformUserLogin> PlatformUserLogins => Set<PlatformUserLogin>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            Assembly.GetExecutingAssembly(),
            t => t.Namespace == "Negosio.Infrastructure.Persistence.Platform");
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TouchTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        TouchTimestamps();
        return base.SaveChanges();
    }

    private void TouchTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(Entity.UpdatedAtUtc)).CurrentValue = now;
            }
        }
    }
}
