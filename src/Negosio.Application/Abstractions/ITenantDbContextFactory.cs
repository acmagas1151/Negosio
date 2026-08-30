namespace Negosio.Application.Abstractions;

/// <summary>
/// Builds a short-lived <c>TenantDbContext</c> outside the request pipeline — for provisioning
/// (before a JWT exists) and for tests. Callers own the returned context and must dispose it.
/// </summary>
public interface ITenantDbContextFactory
{
    /// <summary>Resolve the tenant's connection via <see cref="ITenantConnectionResolver"/> and open a context.</summary>
    Task<ITenantDbContext> CreateAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Open a context on an explicit connection string (provisioning, before routing is Active).</summary>
    ITenantDbContext CreateForConnection(string connectionString);
}
