using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Negosio.Infrastructure.Persistence;

/// <summary>
/// Design-time factories for <c>dotnet ef</c> (migrations). The runtime app configures both contexts
/// through <see cref="DependencyInjection.AddInfrastructure"/>. Connection strings here are only used
/// by <c>database update</c>, never by <c>migrations add</c>.
/// </summary>
public sealed class PlatformDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Platform")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Negosio_Platform;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=true";

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connectionString, sql => sql
                .MigrationsAssembly(typeof(PlatformDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__PlatformMigrationsHistory"))
            .Options;

        return new PlatformDbContext(options);
    }
}

public sealed class TenantDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("NEGOSIO_DESIGN_TENANT_SQL")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Negosio_Tenant_DesignTime;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=true";

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(TenantDbContext).Assembly.FullName))
            .Options;

        return new TenantDbContext(options);
    }
}
