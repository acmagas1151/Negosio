using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Negosio.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against a real SQL Server under the database-per-tenant model:
///  - one platform database (<c>Negosio_Test_Platform_&lt;guid&gt;</c>),
///  - one operational database per tenant registered during the test run
///    (<c>Negosio.&lt;slug&gt;_&lt;branch&gt;_&lt;tenantIdPrefix&gt;</c>), created by the real provisioning workflow.
///
/// Server selection, in order:
///  1. <c>NEGOSIO_TEST_SQL</c> (explicit server connection string), else
///  2. a disposable SQL Server container (Testcontainers; needs Docker), else
///  3. SQL Server LocalDB.
/// </summary>
public sealed class NegosioApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestSigningKey = "integration-tests-signing-key-not-a-secret-000000";

    private MsSqlContainer? _container;

    /// <summary>Connection string to the SQL Server, with no <c>Database</c> — the tenant server template.</summary>
    public string ServerTemplate { get; private set; } = string.Empty;

    public string PlatformDatabaseName { get; private set; } = "Negosio_Test_Platform_" + Guid.NewGuid().ToString("N");

    public string PlatformConnectionString => $"{ServerTemplate.TrimEnd(';', ' ')};Database={PlatformDatabaseName}";

    public string MasterConnectionString => $"{ServerTemplate.TrimEnd(';', ' ')};Database=master";

    public async Task InitializeAsync()
    {
        var explicitServer = Environment.GetEnvironmentVariable("NEGOSIO_TEST_SQL");
        if (!string.IsNullOrWhiteSpace(explicitServer))
        {
            ServerTemplate = StripDatabase(explicitServer);
        }
        else if (await TryStartContainerAsync())
        {
            ServerTemplate = StripDatabase(_container!.GetConnectionString());
        }
        else
        {
            ServerTemplate =
                "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=true";
        }

        using var scope = Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await platform.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await DropTenantDatabasesAsync();
        await DropDatabaseAsync(PlatformDatabaseName);

        await base.DisposeAsync();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = PlatformConnectionString,
                ["TenantDatabases:DefaultServerKey"] = "default",
                ["TenantDatabases:Servers:default"] = ServerTemplate,
                ["Database:MigrateOnStartup"] = "false",
                ["Jwt:Issuer"] = "negosio-api",
                ["Jwt:Audience"] = "negosio-web",
                ["Jwt:SigningKey"] = TestSigningKey,
                ["Jwt:AccessTokenMinutes"] = "60"
            });
        });
    }

    /// <summary>A raw connection to one tenant's operational database (for physical-isolation assertions).</summary>
    public async Task<SqlConnection> OpenTenantConnectionAsync(Guid tenantId)
    {
        string name;
        using (var scope = Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            name = await platform.TenantDatabases
                .Where(d => d.TenantId == tenantId)
                .Select(d => d.DatabaseName)
                .SingleAsync();
        }

        var builder = new SqlConnectionStringBuilder(ServerTemplate) { InitialCatalog = name };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<bool> TryStartContainerAsync()
    {
        try
        {
            _container = new MsSqlBuilder().Build();
            await _container.StartAsync();
            return true;
        }
        catch
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }

            return false;
        }
    }

    private static string StripDatabase(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = string.Empty };
        return builder.ConnectionString;
    }

    private async Task DropTenantDatabasesAsync()
    {
        try
        {
            await using var connection = new SqlConnection(MasterConnectionString);
            await connection.OpenAsync();

            var names = new List<string>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT name FROM sys.databases WHERE name LIKE 'Negosio.%';";
                await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }
            }

            foreach (var name in names)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText =
                    $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];";
                await drop.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        try
        {
            await using var connection = new SqlConnection(MasterConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{databaseName}]; END";
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
