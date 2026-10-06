using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>One ordered line within a <see cref="RestoOrderRound"/>. Every pricing/identity field is
/// a snapshot frozen at add-time via the existing <c>SaleLineCalculator</c> (see the design spec
/// Section 2.1's verified rounding invariant) and never re-derived — settlement copies
/// <see cref="GrossAmount"/>/<see cref="DiscountAmount"/>/<see cref="TaxAmount"/>/<see cref="NetAmount"/>
/// straight into the resulting <c>SaleItem</c>. A void never deletes the row or rewrites
/// <see cref="KitchenStatus"/>'s history — it stays visible in its round for the audit trail, and
/// settlement simply skips voided items when building the Sale (design spec Section 5).</summary>
public class RestoOrderItem : Entity
{
    private readonly List<RestoOrderItemModifier> _modifiers = new();

    private RestoOrderItem()
    {
        ProductNameSnapshot = string.Empty;
        StationNameSnapshot = string.Empty;
    }

    private RestoOrderItem(
        Guid tenantId,
        Guid restoOrderRoundId,
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        Guid stationId,
        string stationNameSnapshot,
        decimal unitPriceSnapshot,
        decimal taxRateSnapshot,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal quantity,
        string? kitchenNote,
        DiscountType discountKind,
        decimal discountValue,
        Guid? discountApprovedByUserId,
        decimal? costPriceSnapshot)
    {
        DiscountKind = discountKind;
        DiscountValue = discountValue;
        DiscountApprovedByUserId = discountApprovedByUserId;
        CostPriceSnapshot = costPriceSnapshot;
        TenantId = tenantId;
        RestoOrderRoundId = restoOrderRoundId;
        ProductVariantId = productVariantId;
        ProductNameSnapshot = productNameSnapshot;
        VariantNameSnapshot = variantNameSnapshot;
        StationId = stationId;
        StationNameSnapshot = stationNameSnapshot;
        UnitPriceSnapshot = unitPriceSnapshot;
        TaxRateSnapshot = taxRateSnapshot;
        GrossAmount = grossAmount;
        DiscountAmount = discountAmount;
        TaxAmount = taxAmount;
        NetAmount = netAmount;
        Quantity = quantity;
        KitchenNote = string.IsNullOrWhiteSpace(kitchenNote) ? null : kitchenNote.Trim();
    }

    public Guid TenantId { get; private set; }

    public Guid RestoOrderRoundId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string? VariantNameSnapshot { get; private set; }

    /// <summary>Retained even if the station is later renamed or disabled — see
    /// <see cref="RestoStation"/>'s doc comment.</summary>
    public Guid StationId { get; private set; }

    public string StationNameSnapshot { get; private set; }

    public decimal UnitPriceSnapshot { get; private set; }

    public decimal TaxRateSnapshot { get; private set; }

    public decimal GrossAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public decimal Quantity { get; private set; }

    public string? KitchenNote { get; private set; }

    /// <summary>Frozen discount input, kept so the Sale item reproduces it exactly (spec G1).</summary>
    public DiscountType DiscountKind { get; private set; }

    public decimal DiscountValue { get; private set; }

    /// <summary>The Manager/Admin/Owner who approved this line's discount, when the applier needed one.</summary>
    public Guid? DiscountApprovedByUserId { get; private set; }

    /// <summary>From the catalog at add-time, so the Sale item carries the same cost (spec G2).</summary>
    public decimal? CostPriceSnapshot { get; private set; }

    public RestoKitchenStatus? KitchenStatus { get; private set; }

    public DateTime? AcknowledgedAtUtc { get; private set; }

    public Guid? AcknowledgedByUserId { get; private set; }

    public DateTime? ReadyAtUtc { get; private set; }

    public Guid? ReadyByUserId { get; private set; }

    public DateTime? ServedAtUtc { get; private set; }

    public Guid? ServedByUserId { get; private set; }

    public DateTime? VoidedAtUtc { get; private set; }

    public Guid? VoidedByUserId { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public string? VoidReason { get; private set; }

    public IReadOnlyCollection<RestoOrderItemModifier> Modifiers => _modifiers.AsReadOnly();

    internal static RestoOrderItem Create(
        Guid tenantId,
        Guid restoOrderRoundId,
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        Guid stationId,
        string stationNameSnapshot,
        decimal unitPriceSnapshot,
        decimal taxRateSnapshot,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal quantity,
        string? kitchenNote,
        DiscountType discountKind,
        decimal discountValue,
        Guid? discountApprovedByUserId,
        decimal? costPriceSnapshot)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (costPriceSnapshot is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(costPriceSnapshot), "Cost price cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(productNameSnapshot))
        {
            throw new ArgumentException("Product name is required.", nameof(productNameSnapshot));
        }

        if (string.IsNullOrWhiteSpace(stationNameSnapshot))
        {
            throw new ArgumentException("Station name is required.", nameof(stationNameSnapshot));
        }

        if (unitPriceSnapshot < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPriceSnapshot), "Unit price cannot be negative.");
        }

        if (grossAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(grossAmount), "Gross amount cannot be negative.");
        }

        if (discountAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(discountAmount), "Discount amount cannot be negative.");
        }

        if (taxAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(taxAmount), "Tax amount cannot be negative.");
        }

        if (netAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(netAmount), "Net amount cannot be negative.");
        }

        return new RestoOrderItem(
            tenantId, restoOrderRoundId, productVariantId, productNameSnapshot, variantNameSnapshot,
            stationId, stationNameSnapshot, unitPriceSnapshot, taxRateSnapshot, grossAmount, discountAmount,
            taxAmount, netAmount, quantity, kitchenNote, discountKind, discountValue, discountApprovedByUserId, costPriceSnapshot);
    }

    /// <summary>Called by <see cref="RestoOrderRound.Release"/> for every non-voided item in the round.</summary>
    internal void EnterKitchenQueue()
    {
        KitchenStatus = RestoKitchenStatus.Pending;
    }

    /// <summary>Adds a frozen modifier line. Only valid before this item has been released to the
    /// kitchen (<see cref="KitchenStatus"/> is still null) and while it hasn't been voided — a
    /// modifier added after firing would silently change what the kitchen sees without ever
    /// reaching them. This domain-only plan does NOT recompute <see cref="UnitPriceSnapshot"/>,
    /// <see cref="GrossAmount"/>, <see cref="DiscountAmount"/>, <see cref="TaxAmount"/>, or
    /// <see cref="NetAmount"/> when a modifier is added: those fields are frozen once, at
    /// <see cref="Create"/>/<see cref="RestoOrderRound.AddItem"/> time. A future M2 application
    /// service is responsible for calling <see cref="AddModifier"/> (if at all) strictly BEFORE the
    /// round is released, and for computing this item's pricing snapshot fields so they already
    /// include every modifier's <see cref="RestoOrderItemModifier.PriceDeltaSnapshot"/> at the moment
    /// it calls <c>AddItem</c> — otherwise the price shown to the customer during ordering can
    /// silently drift from what settlement ultimately charges.</summary>
    public RestoOrderItemModifier AddModifier(string modifierGroupNameSnapshot, string modifierOptionNameSnapshot, decimal priceDeltaSnapshot)
    {
        if (KitchenStatus is not null)
        {
            throw new InvalidOperationException("A modifier cannot be added once the item has been released to the kitchen.");
        }

        if (VoidedAtUtc is not null)
        {
            throw new InvalidOperationException("This item has been voided and cannot be progressed further.");
        }

        if (string.IsNullOrWhiteSpace(modifierGroupNameSnapshot))
        {
            throw new ArgumentException("Modifier group name is required.", nameof(modifierGroupNameSnapshot));
        }

        if (string.IsNullOrWhiteSpace(modifierOptionNameSnapshot))
        {
            throw new ArgumentException("Modifier option name is required.", nameof(modifierOptionNameSnapshot));
        }

        if (priceDeltaSnapshot < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(priceDeltaSnapshot), "A modifier's price delta cannot be negative.");
        }

        var modifier = RestoOrderItemModifier.Create(TenantId, Id, modifierGroupNameSnapshot, modifierOptionNameSnapshot, priceDeltaSnapshot);
        _modifiers.Add(modifier);
        return modifier;
    }

    public void Acknowledge(Guid userId, DateTime nowUtc)
    {
        if (VoidedAtUtc is not null)
        {
            throw new InvalidOperationException("This item has been voided and cannot be progressed further.");
        }

        if (KitchenStatus != RestoKitchenStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending item can be acknowledged.");
        }

        KitchenStatus = RestoKitchenStatus.Acknowledged;
        AcknowledgedAtUtc = nowUtc;
        AcknowledgedByUserId = userId;
        Touch();
    }

    public void MarkReady(Guid userId, DateTime nowUtc)
    {
        if (VoidedAtUtc is not null)
        {
            throw new InvalidOperationException("This item has been voided and cannot be progressed further.");
        }

        if (KitchenStatus != RestoKitchenStatus.Acknowledged)
        {
            throw new InvalidOperationException("Only an acknowledged item can be marked ready.");
        }

        KitchenStatus = RestoKitchenStatus.Ready;
        ReadyAtUtc = nowUtc;
        ReadyByUserId = userId;
        Touch();
    }

    public void MarkServed(Guid userId, DateTime nowUtc)
    {
        if (VoidedAtUtc is not null)
        {
            throw new InvalidOperationException("This item has been voided and cannot be progressed further.");
        }

        if (KitchenStatus != RestoKitchenStatus.Ready)
        {
            throw new InvalidOperationException("Only a ready item can be marked served.");
        }

        KitchenStatus = RestoKitchenStatus.Served;
        ServedAtUtc = nowUtc;
        ServedByUserId = userId;
        Touch();
    }

    /// <summary>Allowed at any <see cref="KitchenStatus"/>, including null (never released) — the only
    /// gate is that the caller must not call this once the parent <c>RestoOrder</c> has been settled,
    /// which this entity cannot check itself (it doesn't hold a reference to its order's status) and
    /// is therefore the calling application service's responsibility, not this method's.</summary>
    public void Void(Guid voidedByUserId, string reason, Guid? approvedByUserId, DateTime nowUtc)
    {
        if (VoidedAtUtc is not null)
        {
            throw new InvalidOperationException("This item has already been voided.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A void reason is required.", nameof(reason));
        }

        VoidedAtUtc = nowUtc;
        VoidedByUserId = voidedByUserId;
        ApprovedByUserId = approvedByUserId;
        VoidReason = reason.Trim();
        Touch();
    }
}
