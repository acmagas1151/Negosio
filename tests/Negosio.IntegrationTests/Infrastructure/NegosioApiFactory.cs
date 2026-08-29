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
/// Boots the real API against a real SQL Server.
///
/// Database selection, in order:
///  1. <c>NEGOSIO_TEST_SQL</c> environment variable (explicit connection string), if set.
///  2. A disposable SQL Server container via Testcontainers (preferred; requires Docker).
///  3. A uniquely-named SQL Server LocalDB database (Windows dev fallback when Docker is unavailable).
///
/// These tests never mock <see cref="AppDbContext"/> - they exercise migrations, constraints and
/// transactions against a live engine.
/// </summary>
public sealed class NegosioApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestSigningKey = "integration-tests-signing-key-not-a-secret-000000";

    private MsSqlContainer? _container;
    private string? _localDbName;

    public string ConnectionString { get; private set; } = string.Empty;

    public string DatabaseMode { get; private set; } = "unknown";

    public async Task InitializeAsync()
    {
        var explicitConnectionString = Environment.GetEnvironmentVariable("NEGOSIO_TEST_SQL");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            ConnectionString = explicitConnectionString;
            DatabaseMode = "explicit";
        }
        else if (await TryStartContainerAsync())
        {
            DatabaseMode = "testcontainers";
        }
        else
        {
            _localDbName = "Negosio_IT_" + Guid.NewGuid().ToString("N");
            ConnectionString =
                $"Server=(localdb)\\MSSQLLocalDB;Database={_localDbName};Trusted_Connection=True;" +
                "MultipleActiveResultSets=true;TrustServerCertificate=true";
            DatabaseMode = "localdb";
        }

        // Build the host (triggers ConfigureWebHost) and apply migrations once.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        else if (_localDbName is not null)
        {
            await DropLocalDbAsync(_localDbName);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["Database:MigrateOnStartup"] = "false",
                ["Jwt:Issuer"] = "negosio-api",
                ["Jwt:Audience"] = "negosio-web",
                ["Jwt:SigningKey"] = TestSigningKey,
                ["Jwt:AccessTokenMinutes"] = "60"
            });
        });
    }

    private async Task<bool> TryStartContainerAsync()
    {
        try
        {
            _container = new MsSqlBuilder().Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
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

    private static async Task DropLocalDbAsync(string databaseName)
    {
        try
        {
            const string master =
                "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=true";
            await using var connection = new SqlConnection(master);
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
