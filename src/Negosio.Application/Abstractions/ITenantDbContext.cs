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

    DbSet<ReceiptSettings> ReceiptSettings { get; }

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

    DbSet<RegisterCashMovement> RegisterCashMovements { get; }

    DbSet<CashDrawerOpenEvent> CashDrawerOpenEvents { get; }

    DbSet<Sale> Sales { get; }

    DbSet<SaleItem> SaleItems { get; }

    DbSet<Payment> Payments { get; }

    DbSet<SaleReturn> SaleReturns { get; }

    DbSet<SaleReturnItem> SaleReturnItems { get; }

    DbSet<RefundPayment> RefundPayments { get; }

    // Delivery
    DbSet<DeliveryReceipt> DeliveryReceipts { get; }

    DbSet<DeliveryReceiptItem> DeliveryReceiptItems { get; }

    DbSet<FulfillmentConversion> FulfillmentConversions { get; }

    DbSet<DocumentNumberCounter> DocumentNumberCounters { get; }

    DbSet<UserPermissionGrant> UserPermissionGrants { get; }

    // RestoPOS
    DbSet<RestoStation> RestoStations { get; }

    DbSet<RestoTable> RestoTables { get; }

    DbSet<ModifierGroup> ModifierGroups { get; }

    DbSet<ModifierOption> ModifierOptions { get; }

    DbSet<ProductModifierGroup> ProductModifierGroups { get; }

    DbSet<RestoOrder> RestoOrders { get; }

    DbSet<RestoOrderRound> RestoOrderRounds { get; }

    DbSet<RestoOrderItem> RestoOrderItems { get; }

    DbSet<RestoOrderItemModifier> RestoOrderItemModifiers { get; }

    DbSet<SaleItemModifier> SaleItemModifiers { get; }

    DatabaseFacade Database { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
