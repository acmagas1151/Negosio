namespace Negosio.Domain.Enums;

/// <summary>State of the physical SQL database backing a tenant. Persisted numerically; values must stay stable.</summary>
public enum TenantDatabaseStatus
{
    Pending = 1,
    Active = 2,
    Failed = 3
}
