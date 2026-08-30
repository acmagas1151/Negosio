namespace Negosio.Application.Abstractions;

/// <summary>A resolved, ready-to-use connection string for a tenant's operational database.</summary>
public sealed record TenantConnection(Guid TenantId, string DatabaseName, string ConnectionString);

/// <summary>
/// Resolves a tenant id to its operational database connection, using the platform routing tables.
/// Validates the tenant exists and is operational; fails safely (throws) for
/// unknown / pending / provisioning / failed / suspended tenants. Connection strings are composed
/// server-side from server configuration + the routed database name — no client input is involved.
/// </summary>
public interface ITenantConnectionResolver
{
    Task<TenantConnection> ResolveAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
