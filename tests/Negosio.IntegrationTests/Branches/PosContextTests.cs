using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Branches;

public class PosContextTests : IntegrationTest
{
    public PosContextTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Context_for_a_cashier_is_their_branch_with_no_picker()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var cashier = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);

        Authorize(cashier);
        var ctx = await Client.GetFromJsonAsync<PosContextDto>("/api/pos/context", TestJson.Options);

        ctx!.CanPickBranch.Should().BeFalse();
        ctx.BranchId.Should().Be(bgc.Id);
        ctx.Branches.Should().ContainSingle();
    }

    [Fact]
    public async Task Context_for_an_owner_with_one_branch_has_no_picker()
    {
        await RegisterLoginAndAuthorizeAsync();

        var ctx = await Client.GetFromJsonAsync<PosContextDto>("/api/pos/context", TestJson.Options);

        ctx!.CanPickBranch.Should().BeFalse();
        ctx.BranchId.Should().NotBeNull();
    }

    [Fact]
    public async Task Context_for_an_owner_with_two_active_branches_offers_a_picker()
    {
        await RegisterLoginAndAuthorizeAsync();
        await CreateBranchAsync("BGC", "BGC");

        var ctx = await Client.GetFromJsonAsync<PosContextDto>("/api/pos/context", TestJson.Options);

        ctx!.CanPickBranch.Should().BeTrue();
        ctx.Branches.Should().HaveCount(2);
        ctx.BranchId.Should().BeNull();
    }

    [Fact]
    public async Task Registers_report_availability_and_ownership()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var r1 = await CreateRegisterAsync(mainId, "R1", "R1");
        var r2 = await CreateRegisterAsync(mainId, "R2", "R2");
        var r3 = await CreateRegisterAsync(mainId, "R3", "R3");

        var cashierA = await AddTenantUserTokenAsync("a@example.com", UserRole.Cashier, mainId);
        var cashierB = await AddTenantUserTokenAsync("b@example.com", UserRole.Cashier, mainId);

        Authorize(cashierA);
        await Client.PostAsJsonAsync("/api/register-sessions/open", new OpenRegisterSessionRequest(r1.Id, 100m));

        Authorize(cashierB);
        var list = await Client.GetFromJsonAsync<List<PosRegisterDto>>("/api/pos/registers", TestJson.Options);

        list!.Single(r => r.Id == r1.Id).OpenSession.Should().NotBeNull();
        list.Single(r => r.Id == r1.Id).OpenSession!.Mine.Should().BeFalse();
        list.Single(r => r.Id == r1.Id).OpenSession!.OpenedByName.Should().Contain("User");
        list.Single(r => r.Id == r2.Id).OpenSession.Should().BeNull();
        list.Single(r => r.Id == r3.Id).OpenSession.Should().BeNull();

        Authorize(cashierA);
        var listA = await Client.GetFromJsonAsync<List<PosRegisterDto>>("/api/pos/registers", TestJson.Options);
        listA!.Single(r => r.Id == r1.Id).OpenSession!.Mine.Should().BeTrue();
    }

    [Fact]
    public async Task Registers_reject_a_foreign_branch_for_a_scoped_user()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var cashier = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);

        Authorize(cashier);
        (await Client.GetAsync($"/api/pos/registers?branchId={mainId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
