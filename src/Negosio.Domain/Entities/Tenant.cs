using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// Platform-level (control-plane) record of a business. Lives in the platform database and carries
/// only identity and provisioning state — its branches, users, catalog, inventory and sales live in
/// that tenant's own operational database (see <c>TenantProfile</c> / <c>TenantDatabase</c>).
/// </summary>
public class Tenant : Entity
{
    private Tenant()
    {
        Name = string.Empty;
    }

    private Tenant(string name, BusinessType businessType)
    {
        Name = name;
        BusinessType = businessType;
        IsActive = true;
        ProvisioningStatus = TenantProvisioningStatus.Pending;
    }

    public string Name { get; private set; }

    public BusinessType BusinessType { get; private set; }

    /// <summary>Soft-disable independent of provisioning (e.g. account closed).</summary>
    public bool IsActive { get; private set; }

    public TenantProvisioningStatus ProvisioningStatus { get; private set; }

    /// <summary>The tenant may run normal operations only when Active and not soft-disabled.</summary>
    public bool IsOperational => IsActive && ProvisioningStatus == TenantProvisioningStatus.Active;

    public static Tenant Create(string name, BusinessType businessType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name is required.", nameof(name));
        }

        return new Tenant(name.Trim(), businessType);
    }

    public void MarkProvisioning()
    {
        ProvisioningStatus = TenantProvisioningStatus.Provisioning;
        Touch();
    }

    public void MarkActive()
    {
        ProvisioningStatus = TenantProvisioningStatus.Active;
        Touch();
    }

    public void MarkFailed()
    {
        ProvisioningStatus = TenantProvisioningStatus.Failed;
        Touch();
    }

    public void Suspend()
    {
        ProvisioningStatus = TenantProvisioningStatus.Suspended;
        Touch();
    }

    public void Reactivate()
    {
        ProvisioningStatus = TenantProvisioningStatus.Active;
        IsActive = true;
        Touch();
    }
}
