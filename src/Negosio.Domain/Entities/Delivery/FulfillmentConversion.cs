using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// An immutable record of one post-sale fulfillment change for one sale line: quantity moving between
/// Delivery and Pickup intent, or being released back to unscheduled. Written in the same transaction as
/// the <see cref="SaleItem.ConvertFulfillment"/> call (or the cancellation) that caused it.
/// <para>This log is what makes the ORIGINAL checkout allocation reconstructable — replaying these
/// events backwards over the current intent columns yields what the cashier chose at checkout — which is
/// why <see cref="SaleItem"/> carries no duplicate "original" columns.</para>
/// <para>Never updated, never deleted. There is no mutator on this type by design.</para>
/// </summary>
public class FulfillmentConversion : Entity
{
    private FulfillmentConversion()
    {
        Reason = string.Empty;
    }

    private FulfillmentConversion(
        Guid tenantId,
        Guid saleId,
        Guid saleItemId,
        decimal quantity,
        FulfillmentMethod fromMethod,
        FulfillmentMethod toMethod,
        Guid? sourceRecordId,
        Guid? replacementRecordId,
        string reason,
        Guid createdByUserId,
        DateTime createdAtUtc)
    {
        TenantId = tenantId;
        SaleId = saleId;
        SaleItemId = saleItemId;
        Quantity = quantity;
        FromMethod = fromMethod;
        ToMethod = toMethod;
        SourceRecordId = sourceRecordId;
        ReplacementRecordId = replacementRecordId;
        Reason = reason;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid TenantId { get; private set; }

    public Guid SaleId { get; private set; }

    public Guid SaleItemId { get; private set; }

    public decimal Quantity { get; private set; }

    public FulfillmentMethod FromMethod { get; private set; }

    /// <summary>Equal to <see cref="FromMethod"/> when the event records a release back to the same
    /// method's unscheduled pool rather than a cross-method move.</summary>
    public FulfillmentMethod ToMethod { get; private set; }

    /// <summary>The schedule this quantity came off — the cancelled row.</summary>
    public Guid? SourceRecordId { get; private set; }

    /// <summary>The schedule this quantity went onto, when the disposition created one immediately. Null
    /// for a plain release back to unscheduled.</summary>
    public Guid? ReplacementRecordId { get; private set; }

    public string Reason { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public static FulfillmentConversion Record(
        Guid tenantId,
        Guid saleId,
        Guid saleItemId,
        decimal quantity,
        FulfillmentMethod fromMethod,
        FulfillmentMethod toMethod,
        Guid? sourceRecordId,
        Guid? replacementRecordId,
        string reason,
        Guid createdByUserId,
        DateTime createdAtUtc)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Conversion quantity must be greater than zero.");
        }

        if (fromMethod == FulfillmentMethod.TakeNow || toMethod == FulfillmentMethod.TakeNow)
        {
            throw new InvalidOperationException(
                "Take-now is a checkout-time method and can never be a conversion source or target.");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
        {
            throw new ArgumentException("A conversion reason is required.", nameof(reason));
        }

        return new FulfillmentConversion(
            tenantId, saleId, saleItemId, quantity, fromMethod, toMethod,
            sourceRecordId, replacementRecordId, trimmedReason, createdByUserId, createdAtUtc);
    }
}
