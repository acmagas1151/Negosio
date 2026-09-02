using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A per-user permission override. Row existence is the sole source of truth — there is no
/// soft-revoke or grant/revoke history; the void's own audit fields already record who acted.
/// A unique (TenantId, UserId, Permission) index prevents duplicates.
/// </summary>
public class UserPermissionGrant : Entity
{
    private UserPermissionGrant()
    {
    }

    private UserPermissionGrant(Guid tenantId, Guid userId, UserPermission permission, Guid grantedByUserId)
    {
        TenantId = tenantId;
        UserId = userId;
        Permission = permission;
        GrantedByUserId = grantedByUserId;
        GrantedAtUtc = DateTime.UtcNow;
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public UserPermission Permission { get; private set; }

    public Guid GrantedByUserId { get; private set; }

    public DateTime GrantedAtUtc { get; private set; }

    public static UserPermissionGrant Grant(Guid tenantId, Guid userId, UserPermission permission, Guid grantedByUserId) =>
        new(tenantId, userId, permission, grantedByUserId);
}
