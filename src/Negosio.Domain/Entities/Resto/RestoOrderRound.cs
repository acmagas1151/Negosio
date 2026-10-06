using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>One "send to kitchen" batch within a <see cref="RestoOrder"/>. Deliberately thin —
/// <see cref="Status"/> only tracks whether this round has been sent at all; the real per-station
/// kitchen workflow lives on each <see cref="RestoOrderItem.KitchenStatus"/> (design spec Section 2,
/// "On your station question").</summary>
public class RestoOrderRound : Entity
{
    private readonly List<RestoOrderItem> _items = new();

    private RestoOrderRound()
    {
    }

    private RestoOrderRound(Guid tenantId, Guid restoOrderId, int roundNumber)
    {
        TenantId = tenantId;
        RestoOrderId = restoOrderId;
        RoundNumber = roundNumber;
        Status = RestoOrderRoundStatus.Draft;
    }

    public Guid TenantId { get; private set; }

    public Guid RestoOrderId { get; private set; }

    public int RoundNumber { get; private set; }

    public RestoOrderRoundStatus Status { get; private set; }

    public DateTime? ReleasedAtUtc { get; private set; }

    public Guid? ReleasedByUserId { get; private set; }

    public IReadOnlyCollection<RestoOrderItem> Items => _items.AsReadOnly();

    internal static RestoOrderRound Create(Guid tenantId, Guid restoOrderId, int roundNumber) =>
        new(tenantId, restoOrderId, roundNumber);

    public RestoOrderItem AddItem(
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
        if (Status != RestoOrderRoundStatus.Draft)
        {
            throw new InvalidOperationException("Items can only be added to a round that hasn't been released yet.");
        }

        var item = RestoOrderItem.Create(
            TenantId, Id, productVariantId, productNameSnapshot, variantNameSnapshot, stationId, stationNameSnapshot,
            unitPriceSnapshot, taxRateSnapshot, grossAmount, discountAmount, taxAmount, netAmount, quantity, kitchenNote,
            discountKind, discountValue, discountApprovedByUserId, costPriceSnapshot);
        _items.Add(item);
        return item;
    }

    /// <summary>Idempotent by design (design spec Section 6.1) — calling this on an already-Released
    /// round is a no-op that preserves who originally released it, so a retried release call (the
    /// manual recovery button, or the background reconciliation worker) never overwrites the audit
    /// trail or double-fires a ticket.</summary>
    public void Release(Guid releasedByUserId, DateTime nowUtc) => ReleaseCore(releasedByUserId, nowUtc);

    /// <summary>Release performed by the PAYO recovery path (no user actor). Same idempotency and kitchen
    /// queueing as <see cref="Release"/>; <see cref="ReleasedByUserId"/> stays null to show the release was
    /// system-initiated.</summary>
    public void ReleaseBySystem(DateTime nowUtc) => ReleaseCore(null, nowUtc);

    private void ReleaseCore(Guid? releasedByUserId, DateTime nowUtc)
    {
        if (Status == RestoOrderRoundStatus.Released)
        {
            return;
        }

        if (Status != RestoOrderRoundStatus.Draft)
        {
            throw new InvalidOperationException("Only a draft round can be released.");
        }

        Status = RestoOrderRoundStatus.Released;
        ReleasedByUserId = releasedByUserId;
        ReleasedAtUtc = nowUtc;
        foreach (var item in _items.Where(i => i.VoidedAtUtc is null))
        {
            item.EnterKitchenQueue();
        }

        Touch();
    }

    /// <summary>The whole round is scrapped before it was ever sent (e.g. a cashier mis-added a round
    /// and clears it). No per-item void events are needed since nothing was ever sent anywhere.</summary>
    public void VoidWhole()
    {
        if (Status != RestoOrderRoundStatus.Draft)
        {
            throw new InvalidOperationException("Only a draft round can be voided outright.");
        }

        Status = RestoOrderRoundStatus.Voided;
        Touch();
    }
}
