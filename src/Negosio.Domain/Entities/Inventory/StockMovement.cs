using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// One row of the append-only inventory ledger: who changed what, when, why, and the quantity
/// before/after. Every change to <see cref="BranchInventory.QuantityOnHand"/> produces exactly one
/// of these. Rows are never updated (<see cref="Entity.UpdatedAtUtc"/> stays equal to
/// <see cref="Entity.CreatedAtUtc"/>).
/// </summary>
public class StockMovement : Entity
{
    private StockMovement()
    {
    }

    private StockMovement(
        Guid tenantId,
        Guid branchId,
        Guid productVariantId,
        StockMovementType type,
        decimal quantity,
        decimal quantityBefore,
        decimal quantityAfter,
        string? reason,
        Guid createdByUserId,
        string? referenceType,
        Guid? referenceId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        ProductVariantId = productVariantId;
        Type = type;
        Quantity = quantity;
        QuantityBefore = quantityBefore;
        QuantityAfter = quantityAfter;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        CreatedByUserId = createdByUserId;
        ReferenceType = string.IsNullOrWhiteSpace(referenceType) ? null : referenceType.Trim();
        ReferenceId = referenceId;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public StockMovementType Type { get; private set; }

    /// <summary>Signed delta: positive for an increase, negative for a decrease.</summary>
    public decimal Quantity { get; private set; }

    public decimal QuantityBefore { get; private set; }

    public decimal QuantityAfter { get; private set; }

    public string? ReferenceType { get; private set; }

    public Guid? ReferenceId { get; private set; }

    public string? Reason { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public static StockMovement Create(
        Guid tenantId,
        Guid branchId,
        Guid productVariantId,
        StockMovementType type,
        decimal quantity,
        decimal quantityBefore,
        decimal quantityAfter,
        string? reason,
        Guid createdByUserId,
        string? referenceType = null,
        Guid? referenceId = null) =>
        new(tenantId, branchId, productVariantId, type, quantity, quantityBefore, quantityAfter,
            reason, createdByUserId, referenceType, referenceId);
}
