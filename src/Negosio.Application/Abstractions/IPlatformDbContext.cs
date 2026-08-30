using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Negosio.Domain.Entities;

namespace Negosio.Application.Abstractions;

/// <summary>
/// Persistence seam for platform (control-plane) data: tenant identity, database routing and the
/// login directory. One fixed database for the whole platform. Business modules must not use this.
/// </summary>
public interface IPlatformDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<TenantDatabase> TenantDatabases { get; }

    DbSet<PlatformUserLogin> PlatformUserLogins { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
