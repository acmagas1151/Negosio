using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Negosio.Domain.Entities;

namespace Negosio.Application.Abstractions;

/// <summary>
/// Persistence seam for operational (business) data. Backed by the current request's tenant database
/// — resolved from the authenticated <c>tenant_id</c> claim, never from client input. Exposes concrete
/// <see cref="DbSet{T}"/>s plus the transaction / save / change-tracking primitives the use cases need.
/// </summary>
public interface ITenantDbContext : IAsyncDisposable
{
    DbSet<TenantProfile> TenantProfiles { get; }

    DbSet<Branch> Branches { get; }

    DbSet<User> Users { get; }

    // Catalog
    DbSet<Category> Categories { get; }

    DbSet<Product> Products { get; }

    DbSet<ProductVariant> ProductVariants { get; }

    // Inventory
    DbSet<BranchInventory> BranchInventories { get; }

    DbSet<StockMovement> StockMovements { get; }

    // Retail POS
    DbSet<Register> Registers { get; }

    DbSet<RegisterSession> RegisterSessions { get; }

    DbSet<Sale> Sales { get; }

    DbSet<SaleItem> SaleItems { get; }

    DbSet<Payment> Payments { get; }

    DbSet<SaleReturn> SaleReturns { get; }

    DbSet<SaleReturnItem> SaleReturnItems { get; }

    DbSet<RefundPayment> RefundPayments { get; }

    DbSet<DocumentNumberCounter> DocumentNumberCounters { get; }

    DbSet<UserPermissionGrant> UserPermissionGrants { get; }

    DatabaseFacade Database { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
