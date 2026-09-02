namespace Negosio.Domain.Enums;

/// <summary>
/// A per-user operational permission override, layered on top of role-based access. Persisted
/// numerically; values must stay stable. Phase 6 introduces exactly one value — this is not a
/// general permissions matrix, and should not grow without a fresh design discussion.
/// </summary>
public enum UserPermission
{
    SalesVoid = 1
}
