using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Resto;

/// <summary>
/// The single way RestoPOS takes an order's pessimistic lock. Every structural write (and settlement) calls
/// this as the first statement of its transaction, so the lock order and the post-lock RowVersion check are
/// identical everywhere. Settlement takes its register-session lock before calling this (spec 6.1).
/// </summary>
internal static class RestoOrderLocking
{
    public static async Task<RestoOrder> LockAndLoadAsync(
        ITenantDbContext db,
        IBranchAccessResolver branchAccess,
        Guid tenantId,
        Guid orderId,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM RestoOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {orderId} AND TenantId = {tenantId}")
            .ToListAsync(cancellationToken);

        var order = await db.RestoOrders
            .Include(o => o.Rounds).ThenInclude(r => r.Items).ThenInclude(i => i.Modifiers)
            .SingleOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RestoOrderNotFound, "Order not found.");

        var assigned = await branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } scoped && scoped != order.BranchId)
        {
            throw new NotFoundException(ErrorCodes.RestoOrderNotFound, "Order not found.");
        }

        if (expectedRowVersion is not null && !expectedRowVersion.AsSpan().SequenceEqual(order.RowVersion))
        {
            throw new ConflictException(ErrorCodes.RestoOrderConcurrencyConflict,
                "This order was changed by someone else. Refresh and try again.");
        }

        return order;
    }
}
