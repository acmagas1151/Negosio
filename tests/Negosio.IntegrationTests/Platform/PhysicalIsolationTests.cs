using System.Net.Http.Json;
using FluentAssertions;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Platform;

/// <summary>
/// Proves the tenancy boundary is physical: one tenant's rows never exist in another tenant's
/// database, verified by connecting straight to each operational database.
/// </summary>
public class PhysicalIsolationTests : IntegrationTest
{
    public PhysicalIsolationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task One_tenants_catalog_is_completely_absent_from_the_others_database()
    {
        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "B Co", email: "b@example.com", branchCode: "B"));

        Authorize(tenantA.AccessToken);
        var categoryA = await CreateCategoryAsync("Drinks A");
        await CreateSimpleProductAsync(categoryA.Id, "Cola A", sku: "A-1");
        await CreateSimpleProductAsync(categoryA.Id, "Water A", sku: "A-2");

        Authorize(tenantB.AccessToken);
        var categoryB = await CreateCategoryAsync("Drinks B");
        await CreateSimpleProductAsync(categoryB.Id, "Cola B", sku: "B-1");

        await using (var aDb = await Factory.OpenTenantConnectionAsync(tenantA.User.TenantId))
        {
            var names = await PlatformSql.StringsAsync(aDb, "SELECT Name FROM Products ORDER BY Name");
            names.Should().BeEquivalentTo(new[] { "Cola A", "Water A" });
            names.Should().NotContain("Cola B");
        }

        await using (var bDb = await Factory.OpenTenantConnectionAsync(tenantB.User.TenantId))
        {
            var names = await PlatformSql.StringsAsync(bDb, "SELECT Name FROM Products ORDER BY Name");
            names.Should().Equal("Cola B");
        }
    }

    [Fact]
    public async Task Each_tenant_database_carries_only_its_own_single_tenant_profile()
    {
        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "Alpha", email: "a@example.com", branchCode: "A"));
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "Beta", email: "b@example.com", branchCode: "B"));

        await using var aDb = await Factory.OpenTenantConnectionAsync(tenantA.User.TenantId);
        (await PlatformSql.StringsAsync(aDb, "SELECT Name FROM TenantProfile"))
            .Should().Equal("Alpha");

        await using var bDb = await Factory.OpenTenantConnectionAsync(tenantB.User.TenantId);
        (await PlatformSql.StringsAsync(bDb, "SELECT Name FROM TenantProfile"))
            .Should().Equal("Beta");
    }
}
