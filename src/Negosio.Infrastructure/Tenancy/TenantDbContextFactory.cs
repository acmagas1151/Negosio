using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Infrastructure.Persistence;

namespace Negosio.Infrastructure.Tenancy;

/// <summary>
/// Builds short-lived <see cref="TenantDbContext"/> instances outside the request pipeline
/// (provisioning, tests). Callers own and dispose the returned context.
/// </summary>
public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantConnectionResolver _resolver;

    public TenantDbContextFactory(ITenantConnectionResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<ITenantDbContext> CreateAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var connection = await _resolver.ResolveAsync(tenantId, cancellationToken);
        return CreateForConnection(connection.ConnectionString);
    }

    public ITenantDbContext CreateForConnection(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(TenantDbContext).Assembly.FullName))
            .Options;

        return new TenantDbContext(options);
    }
}
