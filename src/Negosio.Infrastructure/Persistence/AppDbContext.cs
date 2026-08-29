using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Domain.Common;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IApplicationDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
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
