using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Negosio.Infrastructure.Tenancy;

/// <summary>
/// Resolves a <c>ServerKey</c> to a connection-string *template* (server + auth, no database) from
/// configuration <c>TenantDatabases:Servers:&lt;key&gt;</c>. This is the only place server
/// credentials are read; for Azure this evolves toward Managed Identity / Key Vault.
/// </summary>
public sealed class TenantServerRegistry
{
    private readonly IConfiguration _configuration;

    public TenantServerRegistry(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>The default server key used for newly provisioned tenants.</summary>
    public string DefaultServerKey =>
        _configuration["TenantDatabases:DefaultServerKey"] ?? "default";

    public string GetServerTemplate(string serverKey)
    {
        var template = _configuration[$"TenantDatabases:Servers:{serverKey}"];
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidOperationException(
                $"No connection-string template configured for tenant server '{serverKey}' (TenantDatabases:Servers:{serverKey}).");
        }

        return template;
    }

    /// <summary>Compose a full connection string for a tenant database on the given server.</summary>
    public string BuildConnectionString(string serverKey, string databaseName) =>
        WithDatabase(serverKey, databaseName);

    /// <summary>Connection string to the server's <c>master</c> database (for CREATE DATABASE).</summary>
    public string BuildMasterConnectionString(string serverKey) =>
        WithDatabase(serverKey, "master");

    /// <summary>
    /// Set <c>Initial Catalog</c> on the server template via <see cref="SqlConnectionStringBuilder"/>
    /// so names containing a dot (e.g. <c>Negosio.Acme_MAIN_1A2B3C4D</c>) are quoted correctly.
    /// </summary>
    private string WithDatabase(string serverKey, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(GetServerTemplate(serverKey))
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }
}
