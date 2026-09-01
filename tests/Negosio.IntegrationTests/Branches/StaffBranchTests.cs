using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Auth;
using Negosio.Application.Branches;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Branches;

public class StaffBranchTests : IntegrationTest
{
    public StaffBranchTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<StaffInvitationResultDto> InviteAsync(string email, string role, string? branchId, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await Client.PostAsJsonAsync("/api/staff/invitations", new InviteStaffRequest(email, role, branchId));
        response.StatusCode.Should().Be(expected);
        return expected == HttpStatusCode.Created
            ? (await response.Content.ReadFromJsonAsync<StaffInvitationResultDto>(TestJson.Options))!
            : null!;
    }

    private async Task<LoginResponse> AcceptAndLoginAsync(string acceptPath, string email)
    {
        var owner = Client.DefaultRequestHeaders.Authorization;
        Client.DefaultRequestHeaders.Authorization = null;
        (await Client.PostAsJsonAsync($"/api/auth/invitations/{acceptPath.Split('/').Last()}/accept",
            new AcceptInvitationRequest("Cara", "Cashier", "SecurePassword123!"))).EnsureSuccessStatusCode();
        var login = (await (await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "SecurePassword123!")))
            .Content.ReadFromJsonAsync<LoginResponse>(TestJson.Options))!;
        Client.DefaultRequestHeaders.Authorization = owner;
        return login;
    }

    [Fact]
    public async Task Invite_a_cashier_with_an_active_branch_binds_them_to_it()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var invite = await InviteAsync("cara@example.com", "Cashier", bgc.Id.ToString());
        var login = await AcceptAndLoginAsync(invite.AcceptPath!, "cara@example.com");

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var branches = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);
        branches.Should().ContainSingle().Which.Id.Should().Be(bgc.Id);
    }

    [Fact]
    public async Task Invite_a_cashier_with_an_inactive_branch_is_rejected()
    {
        await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        (await Client.PostAsync($"/api/branches/{bgc.Id}/deactivate", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync("/api/staff/invitations",
            new InviteStaffRequest("cara@example.com", "Cashier", bgc.Id.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("BRANCH_INACTIVE");
    }

    [Fact]
    public async Task Invite_a_cashier_with_no_branch_is_a_validation_error()
    {
        await RegisterLoginAndAuthorizeAsync();
        await InviteAsync("cara@example.com", "Cashier", branchId: null, expected: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invite_an_admin_with_a_branch_is_rejected()
    {
        await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var response = await Client.PostAsJsonAsync("/api/staff/invitations",
            new InviteStaffRequest("adm@example.com", "Admin", bgc.Id.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("BRANCH_FORBIDDEN");
    }

    [Fact]
    public async Task Promoting_an_admin_to_manager_requires_a_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var adminId = await MakeAdminAsync("adm@example.com");

        var noBranch = await Client.PutAsJsonAsync($"/api/staff/{adminId}/role", new ChangeStaffRoleRequest("Manager"));
        noBranch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var withBranch = await Client.PutAsJsonAsync($"/api/staff/{adminId}/role", new ChangeStaffRoleRequest("Manager", mainId.ToString()));
        withBranch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await withBranch.Content.ReadFromJsonAsync<StaffMemberDto>(TestJson.Options))!.BranchId.Should().Be(mainId);
    }

    [Fact]
    public async Task Demoting_a_manager_to_admin_clears_the_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        _ = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainId);
        var mgr = (await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options))!
            .Single(m => m.Email == "mgr@example.com");

        var res = await Client.PutAsJsonAsync($"/api/staff/{mgr.Id}/role", new ChangeStaffRoleRequest("Admin"));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<StaffMemberDto>(TestJson.Options))!.BranchId.Should().BeNull();
    }

    [Fact]
    public async Task Change_branch_moves_the_user_and_takes_effect_immediately()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var caraToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);
        var cara = (await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options))!
            .Single(m => m.Email == "cara@example.com");

        (await Client.PostAsJsonAsync($"/api/staff/{cara.Id}/branch", new ChangeStaffBranchRequest(mainId.ToString())))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", caraToken);
        var branches = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);
        branches.Should().ContainSingle().Which.Id.Should().Be(mainId);
    }

    [Fact]
    public async Task Change_branch_is_blocked_while_the_user_holds_an_open_session()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var register = await CreateRegisterAsync(bgc.Id, "BR", "BR");
        var caraToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);

        Authorize(caraToken);
        await Client.PostAsJsonAsync("/api/register-sessions/open",
            new Negosio.Application.Registers.OpenRegisterSessionRequest(register.Id, 100m));

        Authorize(owner.AccessToken);
        var cara = (await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options))!
            .Single(m => m.Email == "cara@example.com");

        var response = await Client.PostAsJsonAsync($"/api/staff/{cara.Id}/branch", new ChangeStaffBranchRequest(mainId.ToString()));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("STAFF_HAS_OPEN_REGISTER_SESSION");
    }

    private async Task<Guid> MakeAdminAsync(string email)
    {
        var token = await AddTenantUserTokenAsync(email, UserRole.Admin);
        _ = token;
        return (await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options))!
            .Single(m => m.Email == email).Id;
    }
}
