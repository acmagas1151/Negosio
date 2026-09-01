using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Branches;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Branches;

public class BranchScopedAccessTests : IntegrationTest
{
    public BranchScopedAccessTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task A_branch_scoped_user_only_sees_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);

        Authorize(cashierToken);
        var scoped = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);
        scoped.Should().ContainSingle().Which.Id.Should().Be(bgc.Id);

        // even asking for everything only returns their branch
        var scopedAll = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches?includeInactive=true", TestJson.Options);
        scopedAll.Should().ContainSingle().Which.Id.Should().Be(bgc.Id);

        Authorize(owner.AccessToken);
        var all = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches?includeInactive=true", TestJson.Options);
        all.Should().Contain(b => b.Id == mainId).And.Contain(b => b.Id == bgc.Id);
    }

    [Fact]
    public async Task An_unassigned_branch_scoped_user_is_forbidden()
    {
        await RegisterLoginAndAuthorizeAsync();
        var token = await AddTenantUserTokenAsync("noassign@example.com", UserRole.Cashier); // branchId null

        Authorize(token);
        var response = await Client.GetAsync("/api/branches");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("BRANCH_FORBIDDEN");
    }
}
