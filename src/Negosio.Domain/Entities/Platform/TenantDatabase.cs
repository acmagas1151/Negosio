using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// Routing record (platform database) that maps a <see cref="Tenant"/> to its operational SQL
/// database. <see cref="ServerKey"/> names a server/connection configuration entry so that not every
/// tenant database is assumed to live on the same logical server forever (Azure Elastic Pools later).
/// Never stores a password — credentials are resolved from server config / managed identity.
/// </summary>
public class TenantDatabase : Entity
{
    private TenantDatabase()
    {
        DatabaseName = string.Empty;
        ServerKey = string.Empty;
    }

    private TenantDatabase(Guid tenantId, string databaseName, string serverKey)
    {
        TenantId = tenantId;
        DatabaseName = databaseName;
        ServerKey = serverKey;
        Status = TenantDatabaseStatus.Pending;
    }

    public Guid TenantId { get; private set; }

    public string DatabaseName { get; private set; }

    public string ServerKey { get; private set; }

    public TenantDatabaseStatus Status { get; private set; }

    public static TenantDatabase Create(Guid tenantId, string databaseName, string serverKey)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("Database name is required.", nameof(databaseName));
        }

        if (string.IsNullOrWhiteSpace(serverKey))
        {
            throw new ArgumentException("Server key is required.", nameof(serverKey));
        }

        return new TenantDatabase(tenantId, databaseName.Trim(), serverKey.Trim());
    }

    public void MarkActive()
    {
        Status = TenantDatabaseStatus.Active;
        Touch();
    }

    public void MarkFailed()
    {
        Status = TenantDatabaseStatus.Failed;
        Touch();
    }
}
