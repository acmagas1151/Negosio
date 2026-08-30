using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Branches;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Catalog;

public class BranchTests : IntegrationTest
{
    public BranchTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Lists_the_current_tenants_branch()
    {
        await RegisterLoginAndAuthorizeAsync();

        var branches = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);

        branches.Should().ContainSingle();
        branches![0].Name.Should().Be("Main Branch");
        branches[0].Code.Should().Be("MAIN");
        branches[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task A_tenant_never_sees_another_tenants_branches()
    {
        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "AAA"));
        var tenantB = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "B Co", email: "b@example.com", branchCode: "BBB"));

        Authorize(tenantB.AccessToken);
        var bBranches = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);
        var bBranchId = bBranches!.Single().Id;

        Authorize(tenantA.AccessToken);
        var aBranches = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);

        aBranches.Should().ContainSingle();
        aBranches![0].Code.Should().Be("AAA");
        aBranches.Should().NotContain(b => b.Id == bBranchId);
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var response = await Client.GetAsync("/api/branches");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
