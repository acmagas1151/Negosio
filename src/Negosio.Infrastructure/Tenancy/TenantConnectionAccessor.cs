using Negosio.Application.Abstractions;
using Negosio.Application.Common;

namespace Negosio.Infrastructure.Tenancy;

/// <summary>
/// Holds the tenant database connection resolved for the current request (populated by
/// <c>TenantResolutionMiddleware</c>). Scoped — never shared between requests.
/// </summary>
public sealed class TenantConnectionAccessor
{
    private TenantConnection? _connection;

    public bool HasConnection => _connection is not null;

    public void Set(TenantConnection connection) => _connection = connection;

    public TenantConnection Require() =>
        _connection ?? throw new TenantUnavailableException(
            ErrorCodes.TenantUnavailable,
            "No tenant database is available for this request.");
}
