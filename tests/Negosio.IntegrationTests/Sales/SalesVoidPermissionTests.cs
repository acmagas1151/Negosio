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
        new(HttpMethod.Put, $"/api/staff/{userId}/permissions") { Content = JsonContent.Create(new ChangeStaffPermissionsRequest(salesVoid)) };

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
}
