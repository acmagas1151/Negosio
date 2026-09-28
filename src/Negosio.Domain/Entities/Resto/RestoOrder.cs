using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>The pre-Sale aggregate shared by both RestoPOS service types (design spec Section 3/4).
/// Owns one or more <see cref="RestoOrderRound"/>s. The only difference between Pay-as-you-order and
/// Bill-Out is a workflow constraint enforced by the application service that calls this aggregate
/// (PAYO allows exactly one round and forces settlement before release; Bill-Out allows releasing
/// while unpaid and allows more rounds) — this class does not itself enforce that distinction beyond
/// the <see cref="TableId"/> requirement below, since "may this round release yet" depends on
/// <see cref="Sale"/> state this aggregate does not hold.
///
/// <see cref="RowVersion"/> is a client-sent-expected-version conflict signal (design spec Section
/// 6.3) — checked by the application service after it acquires the pessimistic
/// <c>WITH (UPDLOCK, HOLDLOCK)</c> lock on this row, not a substitute for that lock.</summary>
public class RestoOrder : Entity
{
    private readonly List<RestoOrderRound> _rounds = new();

    private RestoOrder()
    {
    }

    private RestoOrder(
        Guid tenantId,
        Guid branchId,
        Guid registerSessionId,
        RestoServiceType serviceType,
        Guid? tableId,
        string? displayLabel,
        Guid openedByUserId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        RegisterSessionId = registerSessionId;
        ServiceType = serviceType;
        TableId = tableId;
        DisplayLabel = string.IsNullOrWhiteSpace(displayLabel) ? null : displayLabel.Trim();
        OpenedByUserId = openedByUserId;
        OpenedAtUtc = DateTime.UtcNow;
        Status = RestoOrderStatus.Open;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid RegisterSessionId { get; private set; }

    public RestoServiceType ServiceType { get; private set; }

    /// <summary>Required for <see cref="RestoServiceType.BillOut"/>, always null for
    /// <see cref="RestoServiceType.PayAsYouOrder"/> — enforced once, at creation, since
    /// <see cref="ServiceType"/> never changes after that.</summary>
    public Guid? TableId { get; private set; }

    public string? DisplayLabel { get; private set; }

    public RestoOrderStatus Status { get; private set; }

    public Guid OpenedByUserId { get; private set; }

    public DateTime OpenedAtUtc { get; private set; }

    public DateTime? SettledAtUtc { get; private set; }

    public Guid? SaleId { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>SQL Server `rowversion` — EF-managed. See design spec Section 6.3 for how the
    /// application service is required to use this.</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public IReadOnlyCollection<RestoOrderRound> Rounds => _rounds.AsReadOnly();

    public static RestoOrder OpenPayAsYouOrder(
        Guid tenantId, Guid branchId, Guid registerSessionId, Guid openedByUserId, string? displayLabel) =>
        new(tenantId, branchId, registerSessionId, RestoServiceType.PayAsYouOrder, tableId: null, displayLabel, openedByUserId);

    public static RestoOrder OpenBillOut(
        Guid tenantId, Guid branchId, Guid registerSessionId, Guid openedByUserId, Guid tableId, string? displayLabel)
    {
        if (tableId == Guid.Empty)
        {
            throw new ArgumentException("A Bill-Out order requires a table.", nameof(tableId));
        }

        return new RestoOrder(tenantId, branchId, registerSessionId, RestoServiceType.BillOut, tableId, displayLabel, openedByUserId);
    }

    public RestoOrderRound OpenNextRound()
    {
        var round = RestoOrderRound.Create(TenantId, Id, _rounds.Count + 1);
        _rounds.Add(round);
        return round;
    }

    /// <summary>Called by the settlement use case once the resulting <see cref="Sale"/> has already
    /// been created in the same transaction. <paramref name="nowUtc"/> is caller-supplied so the
    /// timestamp matches whatever the settlement transaction used elsewhere, exactly like
    /// <see cref="Sale.Void"/>'s own convention.</summary>
    public void Settle(Guid saleId, DateTime nowUtc)
    {
        if (Status != RestoOrderStatus.Open)
        {
            throw new InvalidOperationException("Only an open order can be settled.");
        }

        Status = RestoOrderStatus.Settled;
        SaleId = saleId;
        SettledAtUtc = nowUtc;
        Touch();
    }

    /// <summary>Only valid while every round is still <see cref="RestoOrderRoundStatus.Draft"/> or
    /// <see cref="RestoOrderRoundStatus.Voided"/> — the moment anything has been
    /// <see cref="RestoOrderRoundStatus.Released"/>, food has genuinely been sent to the kitchen and
    /// this is no longer a "nothing happened" cancellation (design spec Section 10, Unresolved
    /// Decision #2). The application service must route that case through Settlement instead.</summary>
    public void Cancel(Guid cancelledByUserId, string reason, DateTime nowUtc)
    {
        if (Status != RestoOrderStatus.Open)
        {
            throw new InvalidOperationException("Only an open order can be cancelled.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        }

        if (_rounds.Any(r => r.Status == RestoOrderRoundStatus.Released))
        {
            throw new InvalidOperationException(
                "This order has a released round and can no longer be cancelled outright — settle it instead.");
        }

        Status = RestoOrderStatus.Cancelled;
        CancelledByUserId = cancelledByUserId;
        CancelReason = reason.Trim();
        CancelledAtUtc = nowUtc;
        Touch();
    }
}
