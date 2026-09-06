using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class SalesVoidPermissionTests : IntegrationTest
{
    public SalesVoidPermissionTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private static HttpRequestMessage PermissionsRequest(Guid userId, bool salesVoid) =>
        new(HttpMethod.Put, $"/api/staff/{userId}/permissions") { Content = JsonContent.Create(new ChangeStaffPermissionsRequest(salesVoid, false, false, false)) };

    private static HttpRequestMessage PermissionsRequest(Guid userId, ChangeStaffPermissionsRequest body) =>
        new(HttpMethod.Put, $"/api/staff/{userId}/permissions") { Content = JsonContent.Create(body) };

    [Fact]
    public async Task Owner_grants_and_revokes_for_any_cashier()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(PermissionsRequest(cashierId, true))).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        list!.Single(m => m.Id == cashierId).SalesVoid.Should().BeTrue();

        (await Client.SendAsync(PermissionsRequest(cashierId, false))).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterRevoke = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        afterRevoke!.Single(m => m.Id == cashierId).SalesVoid.Should().BeFalse();
    }

    [Fact]
    public async Task Manager_grants_only_within_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);

        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, bgc.Id);
        var managerId = await GetUserIdFromTokenAsync(managerToken);
        var bgcCashierToken = await AddTenantUserTokenAsync("bgc.cara@example.com", UserRole.Cashier, bgc.Id);
        var bgcCashierId = await GetUserIdFromTokenAsync(bgcCashierToken);
        var mainCashierToken = await AddTenantUserTokenAsync("main.cara@example.com", UserRole.Cashier, main);
        var mainCashierId = await GetUserIdFromTokenAsync(mainCashierToken);

        Authorize(managerToken);

        (await Client.SendAsync(PermissionsRequest(bgcCashierId, true))).StatusCode.Should().Be(HttpStatusCode.OK);

        var forbidden = await Client.SendAsync(PermissionsRequest(mainCashierId, true));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await forbidden.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.BranchForbidden);
    }

    [Fact]
    public async Task Cashier_cannot_grant_their_own_permission()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        Authorize(cashierToken);
        var res = await Client.SendAsync(PermissionsRequest(cashierId, true));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_reaches_staff_list_scoped_to_their_own_branch_only()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);
        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, bgc.Id);
        await AddTenantUserTokenAsync("main.cara@example.com", UserRole.Cashier, main);

        Authorize(managerToken);
        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);

        // Strictly BGC only — not Owner/Admin (whose BranchId is null) and not MAIN.
        list!.Should().OnlyContain(m => m.BranchId == bgc.Id);
        list!.Should().NotContain(m => m.Role == UserRole.Owner || m.Role == UserRole.Admin);
    }

    [Fact]
    public async Task Granting_a_permission_to_a_non_cashier_role_is_rejected()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, branchId);
        var managerId = await GetUserIdFromTokenAsync(managerToken);

        var res = await Client.SendAsync(PermissionsRequest(managerId, true));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Regression for the gap where ChangeRoleAsync/ChangeBranchAsync left a granted SalesVoid row
    /// untouched — a Cashier moved away from Cashier and later moved back would silently regain
    /// direct void authority with no one having re-granted it.
    /// </summary>
    [Fact]
    public async Task Grant_does_not_silently_survive_a_role_change_away_from_and_back_to_cashier()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(PermissionsRequest(cashierId, true))).StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        granted!.Single(m => m.Id == cashierId).SalesVoid.Should().BeTrue();

        // Away from Cashier ...
        (await Client.PutAsJsonAsync($"/api/staff/{cashierId}/role", new ChangeStaffRoleRequest("Manager", branchId.ToString())))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // ... and back to Cashier. The grant row must NOT have survived the round trip — the next
        // Manager to see them as a Cashier again must have to re-grant it explicitly.
        var backToCashier = await Client.PutAsJsonAsync($"/api/staff/{cashierId}/role", new ChangeStaffRoleRequest("Cashier", branchId.ToString()));
        backToCashier.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        list!.Single(m => m.Id == cashierId).SalesVoid.Should().BeFalse("the old grant must not silently reinstate itself");
    }

    /// <summary>
    /// Regression for the gap where a SalesVoid grant (keyed only on UserId — the model has no branch
    /// dimension of its own) silently followed a Cashier across a branch reassignment, even though the
    /// destination branch's Manager never approved it.
    /// </summary>
    [Fact]
    public async Task Grant_does_not_silently_follow_a_cashier_across_a_branch_change()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(PermissionsRequest(cashierId, true))).StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        granted!.Single(m => m.Id == cashierId).SalesVoid.Should().BeTrue();

        (await Client.PostAsJsonAsync($"/api/staff/{cashierId}/branch", new ChangeStaffBranchRequest(mainId.ToString())))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        list!.Single(m => m.Id == cashierId).SalesVoid.Should()
            .BeFalse("the destination branch's Manager never approved this grant and must decide fresh");
    }

    /// <summary>
    /// Same branch-follows-the-user gap as
    /// <see cref="Grant_does_not_silently_follow_a_cashier_across_a_branch_change"/>, but reached
    /// through the role-change endpoint instead: ChangeRoleAsync can itself move a scoped user's
    /// branch while their role stays Cashier (when request.BranchId names a different active branch),
    /// so that path needs the exact same cleanup as the dedicated branch-change endpoint.
    /// </summary>
    [Fact]
    public async Task Grant_does_not_silently_follow_a_branch_change_made_through_the_role_endpoint()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(PermissionsRequest(cashierId, true))).StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        granted!.Single(m => m.Id == cashierId).SalesVoid.Should().BeTrue();

        // Role stays "Cashier" — only the branch changes, via PUT .../role rather than POST .../branch.
        var res = await Client.PutAsJsonAsync($"/api/staff/{cashierId}/role", new ChangeStaffRoleRequest("Cashier", mainId.ToString()));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<StaffMemberDto>(TestJson.Options))!.BranchId.Should().Be(mainId);

        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        list!.Single(m => m.Id == cashierId).SalesVoid.Should()
            .BeFalse("a branch move made through the role endpoint must be cleaned up exactly like the dedicated branch endpoint");
    }

    /// <summary>
    /// The write path was generalized this pass from a SalesVoid-only service into
    /// <see cref="Negosio.Application.Staff.IUserPermissionGrantService.SetAsync"/>, which can set
    /// every grantable permission in one call. This confirms all four persist independently — setting
    /// one doesn't grant or clobber another — and that an existing SalesVoid grant (the only kind
    /// that could exist before this pass) survives being saved alongside the three new ones untouched.
    /// </summary>
    [Fact]
    public async Task All_four_permissions_can_be_granted_together_and_persist_independently()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        // Pre-existing SalesVoid grant, as if made before this pass shipped.
        (await Client.SendAsync(PermissionsRequest(cashierId, salesVoid: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Now grant SalesReturn and CashDrawerOpen too, leaving DiscountApply ungranted — the
        // request always carries all four current values (mirroring the permissions modal's
        // single "Save changes" submitting every toggle's state at once).
        var res = await Client.SendAsync(PermissionsRequest(
            cashierId, new ChangeStaffPermissionsRequest(SalesVoid: true, SalesReturn: true, DiscountApply: false, CashDrawerOpen: true)));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await res.Content.ReadFromJsonAsync<StaffMemberDto>(TestJson.Options))!;
        updated.SalesVoid.Should().BeTrue();
        updated.SalesReturn.Should().BeTrue();
        updated.DiscountApply.Should().BeFalse();
        updated.CashDrawerOpen.Should().BeTrue();

        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        var row = list!.Single(m => m.Id == cashierId);
        row.SalesVoid.Should().BeTrue();
        row.SalesReturn.Should().BeTrue();
        row.DiscountApply.Should().BeFalse();
        row.CashDrawerOpen.Should().BeTrue();

        // Revoke SalesReturn only — the other three must be unaffected.
        var afterRevoke = await Client.SendAsync(PermissionsRequest(
            cashierId, new ChangeStaffPermissionsRequest(SalesVoid: true, SalesReturn: false, DiscountApply: false, CashDrawerOpen: true)));
        afterRevoke.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterRevokeBody = (await afterRevoke.Content.ReadFromJsonAsync<StaffMemberDto>(TestJson.Options))!;
        afterRevokeBody.SalesVoid.Should().BeTrue();
        afterRevokeBody.SalesReturn.Should().BeFalse("revoking one permission must not touch the others");
        afterRevokeBody.CashDrawerOpen.Should().BeTrue();
    }
}
