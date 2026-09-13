using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            // TEMPORARY (removed by pickup-fulfillment plan Task 7): Tasks 2-6 of that plan add
            // EF-mapped columns ahead of a single combined migration in Task 7, which makes EF Core 9's
            // runtime model/snapshot check throw PendingModelChangesWarning on every MigrateAsync call
            // until that migration lands. Task 7 removes this line once the model and snapshot agree again.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new TenantDbContext(options);
    }
}
