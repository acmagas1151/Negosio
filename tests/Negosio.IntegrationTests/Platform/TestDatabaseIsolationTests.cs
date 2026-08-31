using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Platform;

/// <summary>
/// Guards the test harness itself: cleanup drops only the tenant databases THIS run provisioned,
/// never a blind <c>sys.databases LIKE 'Negosio.%'</c> scan — which on a shared LocalDB instance
/// would also match (and drop) a developer's real tenant databases.
/// </summary>
public class TestDatabaseIsolationTests : IntegrationTest
{
    public TestDatabaseIsolationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Cleanup_drops_only_this_runs_tenant_databases_and_leaves_bystanders_alone()
    {
        // A database that looks exactly like a real developer's tenant DB, but was NOT provisioned
        // by this test run (no row in this run's platform TenantDatabases table).
        const string bystander = "Negosio.NotOurs_DEV_00000000";
        await ExecuteOnMasterAsync(
            $"IF DB_ID(N'{bystander}') IS NULL CREATE DATABASE [{bystander}];");

        try
        {
            await RegisterLoginAndAuthorizeAsync();

            var ourTenantDb = await InPlatformScopeAsync(db =>
                db.TenantDatabases.Select(d => d.DatabaseName).SingleAsync());

            await using (var master = new SqlConnection(Factory.MasterConnectionString))
            {
                await master.OpenAsync();
                (await PlatformSql.DatabaseExistsAsync(master, ourTenantDb)).Should().BeTrue();
                (await PlatformSql.DatabaseExistsAsync(master, bystander)).Should().BeTrue();
            }

            await Factory.DropProvisionedTenantDatabasesAsync();

            await using (var master = new SqlConnection(Factory.MasterConnectionString))
            {
                await master.OpenAsync();
                (await PlatformSql.DatabaseExistsAsync(master, ourTenantDb))
                    .Should().BeFalse("cleanup should drop the tenant DB this run provisioned");
                (await PlatformSql.DatabaseExistsAsync(master, bystander))
                    .Should().BeTrue("cleanup must never touch a database it did not create");
            }
        }
        finally
        {
            await ExecuteOnMasterAsync(
                $"IF DB_ID(N'{bystander}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{bystander}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{bystander}]; END");
        }
    }

    private async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(Factory.MasterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
