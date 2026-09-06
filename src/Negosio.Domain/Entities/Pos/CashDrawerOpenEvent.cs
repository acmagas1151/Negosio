using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// An audit record of a no-sale cash-drawer open against an open register session. Append-only,
/// like <see cref="RegisterCashMovement"/> — but deliberately its own entity rather than a movement:
/// there is no amount and no mandatory reason here, and <see cref="RegisterCashMovement.Create"/>'s
/// own invariants (amount &gt; 0, non-empty reason) make it the wrong fit for a non-monetary event.
/// <see cref="ApprovedByUserId"/> is set only when the requester needed Manager/Admin/Owner
/// approval to proceed (they lacked <c>UserPermission.CashDrawerOpen</c> and aren't themselves
/// Owner/Admin/Manager) — null means they had direct permission.
/// </summary>
public class CashDrawerOpenEvent : Entity
{
    private CashDrawerOpenEvent()
    {
    }

    private CashDrawerOpenEvent(
        Guid tenantId, Guid branchId, Guid registerSessionId, Guid requestedByUserId, Guid? approvedByUserId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        RegisterSessionId = registerSessionId;
        RequestedByUserId = requestedByUserId;
        ApprovedByUserId = approvedByUserId;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid RegisterSessionId { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public static CashDrawerOpenEvent Create(
        Guid tenantId, Guid branchId, Guid registerSessionId, Guid requestedByUserId, Guid? approvedByUserId)
    {
        return new CashDrawerOpenEvent(tenantId, branchId, registerSessionId, requestedByUserId, approvedByUserId);
    }
}
