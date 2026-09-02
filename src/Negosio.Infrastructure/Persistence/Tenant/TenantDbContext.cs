using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Domain.Common;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence;

/// <summary>
/// Operational (business) data for one tenant. Instances are always bound to a single tenant
/// database — resolved per request from the authenticated tenant, or created explicitly during
/// provisioning. The <see cref="ITenantContext"/> guard is defence in depth on top of the physical
/// database boundary.
/// </summary>
public sealed class TenantDbContext : DbContext, ITenantDbContext
{
    private readonly ITenantContext _tenantContext;

    public TenantDbContext(DbContextOptions<TenantDbContext> options)
        : this(options, NullTenantContext.Instance)
    {
    }

    public TenantDbContext(DbContextOptions<TenantDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<TenantProfile> TenantProfiles => Set<TenantProfile>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<BranchInventory> BranchInventories => Set<BranchInventory>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<Register> Registers => Set<Register>();

    public DbSet<RegisterSession> RegisterSessions => Set<RegisterSession>();

    public DbSet<Sale> Sales => Set<Sale>();

    public DbSet<SaleItem> SaleItems => Set<SaleItem>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();

    public DbSet<SaleReturnItem> SaleReturnItems => Set<SaleReturnItem>();

    public DbSet<RefundPayment> RefundPayments => Set<RefundPayment>();

    public DbSet<DocumentNumberCounter> DocumentNumberCounters => Set<DocumentNumberCounter>();

    public DbSet<UserPermissionGrant> UserPermissionGrants => Set<UserPermissionGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Only the operational configurations (not the platform ones, which share this assembly).
        modelBuilder.ApplyConfigurationsFromAssembly(
            Assembly.GetExecutingAssembly(),
            t => t.Namespace == "Negosio.Infrastructure.Persistence.Configurations");
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TouchTimestamps();
        GuardTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        TouchTimestamps();
        GuardTenant();
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

    private void GuardTenant()
    {
        if (!_tenantContext.HasTenant)
        {
            return;
        }

        var expected = _tenantContext.TenantId;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var property = entry.Metadata.FindProperty("TenantId");
            if (property is null)
            {
                continue;
            }

            var value = entry.Property("TenantId").CurrentValue;
            if (value is Guid tenantId && tenantId != expected)
            {
                throw new InvalidOperationException(
                    $"Entity {entry.Metadata.ClrType.Name} has TenantId {tenantId} but the current tenant is {expected}.");
            }
        }
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public static readonly NullTenantContext Instance = new();

        public bool HasTenant => false;

        public Guid TenantId => throw new InvalidOperationException("No tenant in scope.");
    }
}
