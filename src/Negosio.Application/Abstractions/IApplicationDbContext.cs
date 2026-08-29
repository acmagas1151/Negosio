using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Negosio.Domain.Entities;

namespace Negosio.Application.Abstractions;

/// <summary>
/// Persistence seam for the Application layer. Intentionally exposes concrete <see cref="DbSet{T}"/>s
/// (rather than a generic repository) plus the transaction/save primitives the use cases need.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<Branch> Branches { get; }

    DbSet<User> Users { get; }

    // Phase 2: Catalog
    DbSet<Category> Categories { get; }

    DbSet<Product> Products { get; }

    DbSet<ProductVariant> ProductVariants { get; }

    // Phase 2: Inventory
    DbSet<BranchInventory> BranchInventories { get; }

    DbSet<StockMovement> StockMovements { get; }

    // Phase 3: Retail POS
    DbSet<Register> Registers { get; }

    DbSet<RegisterSession> RegisterSessions { get; }

    DbSet<Sale> Sales { get; }

    DbSet<SaleItem> SaleItems { get; }

    DbSet<Payment> Payments { get; }

    DbSet<SaleReturn> SaleReturns { get; }

    DbSet<SaleReturnItem> SaleReturnItems { get; }

    DbSet<RefundPayment> RefundPayments { get; }

    DbSet<DocumentNumberCounter> DocumentNumberCounters { get; }

    DatabaseFacade Database { get; }

    /// <summary>Access to change-tracking for one entity (used to set the original rowversion on adjust).</summary>
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
