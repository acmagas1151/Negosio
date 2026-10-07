using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Resto;

/// <summary>
/// The single operation that moves a Pay-as-you-order round from <c>Draft</c> to <c>Released</c>. The manual
/// release action and the background worker both call it; neither has its own transition logic.
/// </summary>
/// <remarks>
/// Locking follows the register-session-then-order rule shared by settlement and void (spec 6.1): the linked
/// sale's register session is locked first, then the order. Under those locks the linked sale must still be
/// <c>Completed</c>, so a sale voided before kitchen release is never released. The caller owns the
/// transaction; this method only reads, locks, mutates, and saves.
/// </remarks>
internal static class PayoRoundReleaseCore
{
    public static async Task<PayoReleaseResult> ReleaseAsync(
        ITenantDbContext db,
        PayoReleaseCommand command,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var tenantId = command.TenantId;
        var orderId = command.OrderId;

        // 1. Unlocked pre-read only to learn which register session to lock first. Re-verified below.
        var linkedSaleId = await db.RestoOrders.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Id == orderId)
            .Select(o => o.SaleId)
            .SingleOrDefaultAsync(cancellationToken);

        // 2. Session lock first, then the order lock (spec 6.1).
        if (linkedSaleId is { } saleId)
        {
            var sessionId = await db.Sales.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.Id == saleId)
                .Select(s => (Guid?)s.RegisterSessionId)
                .SingleOrDefaultAsync(cancellationToken);
            if (sessionId is { } lockedSession)
            {
                await db.Database.SqlQuery<int>(
                    $"SELECT 1 AS Value FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {lockedSession} AND TenantId = {tenantId}")
                    .ToListAsync(cancellationToken);
            }
        }

        await db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM RestoOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {orderId} AND TenantId = {tenantId}")
            .ToListAsync(cancellationToken);

        // 3. Everything below is evaluated on state read after both locks were taken.
        var order = await db.RestoOrders
            .Include(o => o.Rounds).ThenInclude(r => r.Items)
            .SingleOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return new PayoReleaseResult(PayoReleaseOutcome.NotFound);
        }

        if (command.BranchScope is { } scope && order.BranchId != scope)
        {
            return new PayoReleaseResult(PayoReleaseOutcome.NotFound);
        }

        if (command.ExpectedRowVersion is { } expected && !expected.AsSpan().SequenceEqual(order.RowVersion))
        {
            return new PayoReleaseResult(PayoReleaseOutcome.RowVersionMismatch);
        }

        var round = order.Rounds.SingleOrDefault(r => r.Id == command.RoundId);
        if (round is null)
        {
            return new PayoReleaseResult(PayoReleaseOutcome.RoundNotFound);
        }

        if (round.Status == RestoOrderRoundStatus.Released)
        {
            return new PayoReleaseResult(PayoReleaseOutcome.AlreadyReleased, Order: order);
        }

        if (round.Status == RestoOrderRoundStatus.Voided)
        {
            return Refused(PayoRefusalReason.RoundVoided);
        }

        // From here the round is Draft. Bill-Out releases while the order is open, with no sale yet (spec 6.4).
        if (order.ServiceType == RestoServiceType.BillOut)
        {
            if (order.Status != RestoOrderStatus.Open)
            {
                return Refused(PayoRefusalReason.OrderNotReleasable);
            }
        }
        else
        {
            // Pay-as-you-order: the round is released only after settlement, and only while its sale is still Completed.
            if (order.Status != RestoOrderStatus.Settled)
            {
                return Refused(PayoRefusalReason.OrderNotReleasable);
            }

            if (order.SaleId is not { } orderSaleId)
            {
                return Refused(PayoRefusalReason.SaleMissing);
            }

            var saleStatus = await db.Sales.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.Id == orderSaleId)
                .Select(s => (SaleStatus?)s.Status)
                .SingleOrDefaultAsync(cancellationToken);
            if (saleStatus is null)
            {
                return Refused(PayoRefusalReason.SaleMissing);
            }

            if (saleStatus != SaleStatus.Completed)
            {
                return Refused(PayoRefusalReason.SaleNotCompleted);
            }

            if (!round.Items.Any(i => i.VoidedAtUtc is null))
            {
                return Refused(PayoRefusalReason.NoBillableItems);
            }
        }

        // 4. The only transition. Manual passes a user actor; the worker passes null (system release).
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (command.ActorUserId is { } actor)
        {
            round.Release(actor, now);
        }
        else
        {
            round.ReleaseBySystem(now);
        }

        order.RecordStructuralChange();
        await db.SaveChangesAsync(cancellationToken);
        return new PayoReleaseResult(PayoReleaseOutcome.Released, Order: order);
    }

    private static PayoReleaseResult Refused(PayoRefusalReason reason) =>
        new(PayoReleaseOutcome.Refused, reason);
}

/// <summary>Who or what is asking for a release. Manual calls carry the user, branch scope, and RowVersion; worker calls carry none.</summary>
internal sealed record PayoReleaseCommand(
    Guid TenantId,
    Guid OrderId,
    Guid RoundId,
    Guid? ActorUserId,
    byte[]? ExpectedRowVersion,
    Guid? BranchScope);

internal enum PayoReleaseOutcome
{
    Released,
    AlreadyReleased,
    Refused,
    NotFound,
    RoundNotFound,
    RowVersionMismatch,
}

internal enum PayoRefusalReason
{
    None,
    OrderNotReleasable,
    RoundVoided,
    SaleMissing,
    SaleNotCompleted,
    NoBillableItems,
}

internal sealed record PayoReleaseResult(
    PayoReleaseOutcome Outcome,
    PayoRefusalReason Refusal = PayoRefusalReason.None,
    RestoOrder? Order = null);
