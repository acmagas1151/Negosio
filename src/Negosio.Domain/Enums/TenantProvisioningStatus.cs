namespace Negosio.Domain.Enums;

/// <summary>
/// Lifecycle of a tenant's operational database. Normal tenant operations are permitted only in
/// <see cref="Active"/>. Persisted numerically; values must stay stable.
/// </summary>
public enum TenantProvisioningStatus
{
    Pending = 1,
    Provisioning = 2,
    Active = 3,
    Failed = 4,
    Suspended = 5
}
