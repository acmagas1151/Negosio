using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Platform;

public class ProvisioningTests : IntegrationTest
{
    public ProvisioningTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Registration_creates_the_control_plane_rows_and_a_dedicated_database()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest(businessType: "Retail"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponseBody>())!;

        await InPlatformScopeAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == body.TenantId);
            tenant.ProvisioningStatus.Should().Be(TenantProvisioningStatus.Active);

            var mapping = await db.TenantDatabases.SingleAsync(d => d.TenantId == body.TenantId);
            mapping.Status.Should().Be(TenantDatabaseStatus.Active);
            // Negosio.<sanitized business name>_<sanitized initial branch code>_<8-char tenant-id prefix>.
            // NewRegisterRequest defaults: business "Bruno's Cafe", branch code "MAIN".
            mapping.DatabaseName.Should().Be(
                "Negosio.BrunosCafe_MAIN_" + body.TenantId.ToString("N").ToUpperInvariant()[..8]);

            (await db.PlatformUserLogins.SingleAsync(l => l.TenantId == body.TenantId)).EmailNormalized
                .Should().Be("owner@example.com");
            return true;
        });

        // The operational schema really exists in its own database.
        await using var tenantDb = await Factory.OpenTenantConnectionAsync(body.TenantId);
        (await PlatformSql.StringsAsync(tenantDb,
                "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId"))
            .Should().Contain(m => m.EndsWith("_TenantBaseline"));

        (await PlatformSql.CountAsync(tenantDb, "Branches")).Should().Be(1);
        (await PlatformSql.CountAsync(tenantDb, "Users")).Should().Be(1);
        (await PlatformSql.CountAsync(tenantDb, "TenantProfile")).Should().Be(1);
        // A representative Phase 2/3 table proves the baseline is the full operational schema.
        (await PlatformSql.CountAsync(tenantDb, "Sales")).Should().Be(0);
    }

    [Fact]
    public async Task Each_tenant_is_provisioned_into_a_separate_database()
    {
        var a = (await (await Client.PostAsJsonAsync("/api/auth/register",
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A")))
            .Content.ReadFromJsonAsync<RegisterResponseBody>())!;
        var b = (await (await Client.PostAsJsonAsync("/api/auth/register",
            NewRegisterRequest(businessName: "B Co", email: "b@example.com", branchCode: "B")))
            .Content.ReadFromJsonAsync<RegisterResponseBody>())!;

        a.TenantId.Should().NotBe(b.TenantId);

        await using var master = new Microsoft.Data.SqlClient.SqlConnection(Factory.MasterConnectionString);
        await master.OpenAsync();
        (await PlatformSql.DatabaseExistsAsync(master, "Negosio.ACo_A_" + a.TenantId.ToString("N").ToUpperInvariant()[..8]))
            .Should().BeTrue();
        (await PlatformSql.DatabaseExistsAsync(master, "Negosio.BCo_B_" + b.TenantId.ToString("N").ToUpperInvariant()[..8]))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Database_name_sanitizes_hostile_business_and_branch_input()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest(
            businessName: "  Joe's Pizza & Pasta!! (Downtown) — 🍕  ",
            businessType: "FoodAndBeverage",
            branchCode: "hq/main-01"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponseBody>())!;

        var name = await InPlatformScopeAsync(db =>
            db.TenantDatabases.Where(d => d.TenantId == body.TenantId).Select(d => d.DatabaseName).SingleAsync());

        // Everything outside [A-Za-z0-9] stripped, casing preserved, 8-char tenant-id suffix appended.
        name.Should().Be("Negosio.JoesPizzaPastaDowntown_hqmain01_" + body.TenantId.ToString("N").ToUpperInvariant()[..8]);

        // The database with that exact (dotted) name really exists.
        await using var master = new Microsoft.Data.SqlClient.SqlConnection(Factory.MasterConnectionString);
        await master.OpenAsync();
        (await PlatformSql.DatabaseExistsAsync(master, name)).Should().BeTrue();
    }

    [Fact]
    public async Task Re_registering_a_failed_tenant_resumes_provisioning_idempotently()
    {
        var first = (await (await Client.PostAsJsonAsync("/api/auth/register",
            NewRegisterRequest(businessType: "Retail")))
            .Content.ReadFromJsonAsync<RegisterResponseBody>())!;

        // Simulate a provisioning run that died before completing.
        await InPlatformScopeAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == first.TenantId);
            tenant.MarkFailed();
            var mapping = await db.TenantDatabases.SingleAsync(d => d.TenantId == first.TenantId);
            mapping.MarkFailed();
            await db.SaveChangesAsync();
            return true;
        });

        // The same email retries and the workflow resumes rather than erroring.
        var retry = await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest(businessType: "Retail"));
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = (await retry.Content.ReadFromJsonAsync<RegisterResponseBody>())!;
        second.TenantId.Should().Be(first.TenantId);

        await InPlatformScopeAsync(async db =>
        {
            (await db.Tenants.CountAsync()).Should().Be(1);
            (await db.PlatformUserLogins.CountAsync()).Should().Be(1);
            (await db.Tenants.SingleAsync()).ProvisioningStatus.Should().Be(TenantProvisioningStatus.Active);
            return true;
        });

        await InTenantScopeAsync(first.TenantId, async db =>
        {
            (await db.Branches.CountAsync()).Should().Be(1);
            (await db.Users.CountAsync()).Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task A_finished_account_still_rejects_a_duplicate_email()
    {
        await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest(businessType: "Retail"));

        var duplicate = await Client.PostAsJsonAsync("/api/auth/register",
            NewRegisterRequest(businessName: "Impostor", businessType: "Retail", branchCode: "X"));

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("DUPLICATE_EMAIL");
    }
}
