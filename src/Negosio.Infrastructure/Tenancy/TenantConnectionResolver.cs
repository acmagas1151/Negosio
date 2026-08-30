using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Enums;
using Negosio.Infrastructure.Persistence;

namespace Negosio.Infrastructure.Tenancy;

public sealed class TenantConnectionResolver : ITenantConnectionResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly PlatformDbContext _platform;
    private readonly TenantServerRegistry _servers;
    private readonly IMemoryCache _cache;

    public TenantConnectionResolver(PlatformDbContext platform, TenantServerRegistry servers, IMemoryCache cache)
    {
        _platform = platform;
        _servers = servers;
        _cache = cache;
    }

    private sealed record Route(string DatabaseName, string ServerKey, TenantProvisioningStatus TenantStatus, bool TenantActive);

    public async Task<TenantConnection> ResolveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var route = await GetRouteAsync(tenantId, cancellationToken);

        if (route is null)
        {
            throw new NotFoundException(ErrorCodes.TenantNotFound, "The tenant could not be found.");
        }

        if (!route.TenantActive || route.TenantStatus == TenantProvisioningStatus.Suspended)
        {
            throw new TenantUnavailableException(ErrorCodes.TenantSuspended, "This business account is suspended.");
        }

        if (route.TenantStatus != TenantProvisioningStatus.Active)
        {
            throw new TenantUnavailableException(ErrorCodes.TenantUnavailable, "This business is not ready yet. Please try again shortly.");
        }

        var connectionString = _servers.BuildConnectionString(route.ServerKey, route.DatabaseName);
        return new TenantConnection(tenantId, route.DatabaseName, connectionString);
    }

    private Task<Route?> GetRouteAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync($"tenant-route:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            return await (
                from t in _platform.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
                join d in _platform.TenantDatabases.AsNoTracking() on t.Id equals d.TenantId
                select new Route(d.DatabaseName, d.ServerKey, t.ProvisioningStatus, t.IsActive))
                .SingleOrDefaultAsync(cancellationToken);
        });
}
