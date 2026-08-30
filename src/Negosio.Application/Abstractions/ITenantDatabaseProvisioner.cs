namespace Negosio.Application.Abstractions;

/// <summary>
/// Server-side creation and addressing of tenant databases. The database name is composed server-side
/// from a sanitized business name, a sanitized initial branch code, and an id-derived suffix; it is
/// validated against a strict pattern before any DDL runs. Raw user input never reaches a DDL string.
/// </summary>
public interface ITenantDatabaseProvisioner
{
    /// <summary>The server-config key new tenants are placed on.</summary>
    string DefaultServerKey { get; }

    /// <summary>
    /// Human-readable, collision-proof database name for a tenant:
    /// <c>Negosio.&lt;Slug&gt;_&lt;BranchCode&gt;_&lt;TenantIdPrefix8&gt;</c>. The business name and
    /// branch code are sanitized to <c>[A-Za-z0-9]</c> and truncated; the 8-char uppercase tenant-id
    /// prefix guarantees uniqueness. The result is fixed at provisioning time and never changes even
    /// if the business name or initial branch code is later edited.
    /// </summary>
    string DatabaseNameFor(Guid tenantId, string businessName, string initialBranchCode);

    /// <summary>Compose a full connection string for a tenant database.</summary>
    string BuildConnectionString(string serverKey, string databaseName);

    /// <summary>Create the database if it does not exist. Idempotent.</summary>
    Task EnsureDatabaseAsync(string serverKey, string databaseName, CancellationToken cancellationToken = default);
}
