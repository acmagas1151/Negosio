using Negosio.Domain.Enums;

namespace Negosio.Application.Catalog;

/// <summary>
/// The canonical role sets for Phase 2 catalog/inventory access. Defined here (Application layer) so
/// both the API authorization policies and the service-level cost-price redaction use one source.
/// Phase 1 keeps its simple role checks; this is not a permission engine.
/// </summary>
public static class CatalogAccess
{
    /// <summary>May create/update/deactivate categories, products and variants.</summary>
    public static readonly IReadOnlySet<UserRole> CatalogWriterRoles =
        new HashSet<UserRole> { UserRole.Owner, UserRole.Admin, UserRole.Manager };

    /// <summary>May post inventory adjustments.</summary>
    public static readonly IReadOnlySet<UserRole> InventoryWriterRoles =
        new HashSet<UserRole> { UserRole.Owner, UserRole.Admin, UserRole.Manager, UserRole.InventoryStaff };

    /// <summary>May see cost-price figures. Cashier / KitchenStaff / Viewer see selling price only.</summary>
    public static readonly IReadOnlySet<UserRole> CostReaderRoles =
        new HashSet<UserRole> { UserRole.Owner, UserRole.Admin, UserRole.Manager, UserRole.InventoryStaff };

    public static bool CanWriteCatalog(UserRole role) => CatalogWriterRoles.Contains(role);

    public static bool CanWriteInventory(UserRole role) => InventoryWriterRoles.Contains(role);

    public static bool CanViewCost(UserRole role) => CostReaderRoles.Contains(role);
}
