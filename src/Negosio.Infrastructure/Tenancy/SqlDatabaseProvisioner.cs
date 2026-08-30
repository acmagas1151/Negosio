using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Negosio.Application.Abstractions;

namespace Negosio.Infrastructure.Tenancy;

/// <summary>
/// Creates a tenant's physical SQL Server database. The database name is composed server-side as
/// <c>Negosio.&lt;Slug&gt;_&lt;BranchCode&gt;_&lt;TenantIdPrefix8&gt;</c> — the business name and
/// initial branch code are sanitized to <c>[A-Za-z0-9]</c> and truncated, and an 8-char uppercase
/// tenant-id prefix guarantees uniqueness. The final name is validated against a strict pattern and
/// is always bracket-quoted. Raw user input never reaches a CREATE DATABASE statement.
/// </summary>
public sealed partial class SqlDatabaseProvisioner : ITenantDatabaseProvisioner
{
    private const int MaxSlugLength = 40;
    private const int MaxBranchCodeLength = 16;

    private readonly TenantServerRegistry _servers;

    public SqlDatabaseProvisioner(TenantServerRegistry servers)
    {
        _servers = servers;
    }

    public string DefaultServerKey => _servers.DefaultServerKey;

    public string DatabaseNameFor(Guid tenantId, string businessName, string initialBranchCode)
    {
        var slug = Sanitize(businessName, MaxSlugLength, fallback: "Tenant");
        var branch = Sanitize(initialBranchCode, MaxBranchCodeLength, fallback: "MAIN");
        var suffix = tenantId.ToString("N").ToUpperInvariant()[..8];
        return $"Negosio.{slug}_{branch}_{suffix}";
    }

    public string BuildConnectionString(string serverKey, string databaseName) =>
        _servers.BuildConnectionString(serverKey, databaseName);

    public async Task EnsureDatabaseAsync(string serverKey, string databaseName, CancellationToken cancellationToken = default)
    {
        if (!DatabaseNameRegex().IsMatch(databaseName))
        {
            throw new InvalidOperationException($"Refusing to create database with unexpected name '{databaseName}'.");
        }

        var master = _servers.BuildMasterConnectionString(serverKey);
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // Name is regex-validated above and bracket-quoted; DDL cannot parameterise identifiers.
        command.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Keep only ASCII letters/digits, cap the length, and fall back when nothing survives.</summary>
    private static string Sanitize(string? value, int maxLength, string fallback)
    {
        var cleaned = NonAlphanumericRegex().Replace(value ?? string.Empty, string.Empty);
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    [GeneratedRegex(@"^Negosio\.[A-Za-z0-9]{1,40}_[A-Za-z0-9]{1,16}_[A-F0-9]{8}$")]
    private static partial Regex DatabaseNameRegex();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}
