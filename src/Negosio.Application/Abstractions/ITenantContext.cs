namespace Negosio.Application.Abstractions;

/// <summary>
/// The tenant the current request operates against. Derived from the authenticated <c>tenant_id</c>
/// claim (never client input). Used to validate that rows written through <c>ITenantDbContext</c>
/// belong to this tenant (defence in depth alongside the physical database boundary).
/// </summary>
public interface ITenantContext
{
    bool HasTenant { get; }

    Guid TenantId { get; }
}
