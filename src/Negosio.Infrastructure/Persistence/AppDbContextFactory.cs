using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Negosio.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> at design time (migrations). The runtime app configures the context
/// through <see cref="DependencyInjection.AddInfrastructure"/> instead.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Negosio;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=true";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .Options;

        return new AppDbContext(options);
    }
}
