using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Auth;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Staff;

public class StaffTests : IntegrationTest
{
    public StaffTests(NegosioApiFactory factory) : base(factory)
    {
    }

    // ---- helpers -------------------------------------------------------------

    private async Task<StaffInvitationResultDto> InviteAsync(string email, UserRole role, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await Client.PostAsJsonAsync("/api/staff/invitations", new InviteStaffRequest(email, role.ToString()));
        response.StatusCode.Should().Be(expected);
        return expected == HttpStatusCode.Created
            ? (await response.Content.ReadFromJsonAsync<StaffInvitationResultDto>(TestJson.Options))!
            : null!;
    }

    private static string TokenFromPath(string acceptPath) => acceptPath.Split('/').Last();

    private async Task<HttpResponseMessage> AcceptAsync(string token, string first = "Sam", string last = "Staff", string password = "SecurePassword123!")
    {
        var response = await Client.PostAsJsonAsync($"/api/auth/invitations/{token}/accept",
            new AcceptInvitationRequest(first, last, password));

        // Phase 5 interim: invitations don't carry a branch yet (Task 10). Bind any newly-accepted
        // branch-scoped user to the tenant's sole branch so they can authenticate.
        if (response.IsSuccessStatusCode)
        {
            await InScopeAsync(async db =>
            {
                var branchId = await db.Branches.Select(b => b.Id).FirstAsync();
                foreach (var u in await db.Users.Where(u => u.BranchId == null && u.Role != UserRole.Owner && u.Role != UserRole.Admin).ToListAsync())
                {
                    u.AssignBranch(branchId);
                }
                await db.SaveChangesAsync();
                return true;
            });
        }

        return response;
    }

    private async Task<LoginResponse> LoginAsync(string email, string password = "SecurePassword123!")
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(TestJson.Options))!;
    }

    private void ClearAuth() => Client.DefaultRequestHeaders.Authorization = null;

    /// <summary>Invite + accept + return the new member's login. Leaves the Owner authorized on the client.</summary>
    private async Task<(LoginResponse Login, Guid UserId)> AddStaffAsync(string email, UserRole role)
    {
        var invite = await InviteAsync(email, role);
        invite.AcceptPath.Should().NotBeNull("the accept link is exposed in the Testing/Development environment");
        var ownerAuth = Client.DefaultRequestHeaders.Authorization;

        ClearAuth();
        (await AcceptAsync(TokenFromPath(invite.AcceptPath!))).EnsureSuccessStatusCode();

        // Phase 5: branch-scoped roles must have a branch. Bind to the tenant's sole branch.
        if (role is not (UserRole.Owner or UserRole.Admin))
        {
            await InScopeAsync(async db =>
            {
                var user = await db.Users.SingleAsync(u => u.Email == email);
                var branchId = await db.Branches.Select(b => b.Id).FirstAsync();
                user.AssignBranch(branchId);
                await db.SaveChangesAsync();
                return true;
            });
        }

        var login = await LoginAsync(email);

        Client.DefaultRequestHeaders.Authorization = ownerAuth;
        return (login, login.User.Id);
    }

    // ---- invitation flow ---------------------------------------------------

    [Fact]
    public async Task Owner_invites_Cashier_who_accepts_and_can_log_in()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();

        var invite = await InviteAsync("cashier@example.com", UserRole.Cashier);
        invite.Email.Should().Be("cashier@example.com");
        invite.Role.Should().Be(UserRole.Cashier);
        invite.AcceptPath.Should().StartWith("/invite/");

        var token = TokenFromPath(invite.AcceptPath!);

        // preview (anonymous)
        ClearAuth();
        var preview = await Client.GetFromJsonAsync<InvitationPreviewDto>($"/api/auth/invitations/{token}", TestJson.Options);
        preview!.Email.Should().Be("cashier@example.com");
        preview.Role.Should().Be(UserRole.Cashier);
        preview.BusinessName.Should().Be("Bruno's Cafe");

        // accept
        var accept = await AcceptAsync(token);
        accept.EnsureSuccessStatusCode();

        // platform login + tenant user both exist with role Cashier
        var loginRow = await InPlatformScopeAsync(db =>
            db.PlatformUserLogins.SingleAsync(l => l.EmailNormalized == "cashier@example.com"));
        loginRow.Role.Should().Be(UserRole.Cashier);
        loginRow.TenantId.Should().Be(owner.User.TenantId);

        var userRow = await InTenantScopeAsync(owner.User.TenantId, db =>
            db.Users.SingleAsync(u => u.Email == "cashier@example.com"));
        userRow.Id.Should().Be(loginRow.Id);
        userRow.Role.Should().Be(UserRole.Cashier);
        userRow.FirstName.Should().Be("Sam");

        var cashierLogin = await LoginAsync("cashier@example.com");
        cashierLogin.User.Role.Should().Be(UserRole.Cashier);
        cashierLogin.User.TenantId.Should().Be(owner.User.TenantId);
    }

    [Fact]
    public async Task Invitation_token_is_single_use()
    {
        await RegisterLoginAndAuthorizeAsync();
        var invite = await InviteAsync("once@example.com", UserRole.Cashier);
        var token = TokenFromPath(invite.AcceptPath!);
        ClearAuth();

        (await AcceptAsync(token)).EnsureSuccessStatusCode();

        var second = await AcceptAsync(token, first: "Someone", last: "Else");
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await second.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INVITATION_ALREADY_ACCEPTED");
    }

    [Fact]
    public async Task Revoked_invitation_cannot_be_previewed_or_accepted()
    {
        await RegisterLoginAndAuthorizeAsync();
        var invite = await InviteAsync("revoke@example.com", UserRole.Viewer);
        var token = TokenFromPath(invite.AcceptPath!);

        (await Client.DeleteAsync($"/api/staff/invitations/{invite.InvitationId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        ClearAuth();
        (await Client.GetAsync($"/api/auth/invitations/{token}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AcceptAsync(token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_token_is_a_clean_404()
    {
        var response = await Client.GetAsync("/api/auth/invitations/not-a-real-token");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INVITATION_NOT_FOUND");
    }

    // ---- one email = one tenant ------------------------------------------

    [Fact]
    public async Task An_email_that_already_has_an_account_cannot_be_invited_into_another_tenant()
    {
        var tenantA = await RegisterLoginAndAuthorizeAsync(NewRegisterRequest(businessName: "A Co", email: "shared@example.com", branchCode: "A"));

        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "B Co", email: "ownerb@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);

        var response = await Client.PostAsJsonAsync("/api/staff/invitations",
            new InviteStaffRequest("shared@example.com", UserRole.Cashier.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("STAFF_EMAIL_IN_USE");
    }

    // ---- authorization --------------------------------------------------

    [Fact]
    public async Task Owner_and_Admin_may_list_staff_others_may_not()
    {
        await RegisterLoginAndAuthorizeAsync();
        (await Client.GetAsync("/api/staff")).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var role in new[] { UserRole.Manager, UserRole.Cashier, UserRole.InventoryStaff, UserRole.Viewer })
        {
            var token = await AddTenantUserTokenAsync($"{role}@example.com", role);
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await Client.GetAsync("/api/staff")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Admin_can_manage_staff_but_never_creates_an_Admin_or_Owner()
    {
        await RegisterLoginAndAuthorizeAsync();
        var adminToken = await AddTenantUserTokenAsync("admin@example.com", UserRole.Admin);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Admin can invite a Cashier
        (await Client.PostAsJsonAsync("/api/staff/invitations", new InviteStaffRequest("c@example.com", "Cashier")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Admin cannot invite an Admin
        var asAdmin = await Client.PostAsJsonAsync("/api/staff/invitations", new InviteStaffRequest("a2@example.com", "Admin"));
        asAdmin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await asAdmin.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("ROLE_NOT_ASSIGNABLE");

        // Admin cannot invite an Owner
        var asOwner = await Client.PostAsJsonAsync("/api/staff/invitations", new InviteStaffRequest("o2@example.com", "Owner"));
        asOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await asOwner.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("OWNER_ROLE_FORBIDDEN");
    }

    [Fact]
    public async Task Admin_cannot_change_or_deactivate_the_Owner()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var adminToken = await AddTenantUserTokenAsync("admin@example.com", UserRole.Admin);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var reRole = await Client.PutAsJsonAsync($"/api/staff/{owner.User.Id}/role", new ChangeStaffRoleRequest("Manager"));
        reRole.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reRole.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("OWNER_PROTECTED");

        var deact = await Client.PostAsync($"/api/staff/{owner.User.Id}/deactivate", null);
        deact.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await deact.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("OWNER_PROTECTED");
    }

    [Fact]
    public async Task Owner_cannot_deactivate_or_re_role_themselves()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();

        var deact = await Client.PostAsync($"/api/staff/{owner.User.Id}/deactivate", null);
        deact.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await deact.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("STAFF_SELF_ACTION");

        var reRole = await Client.PutAsJsonAsync($"/api/staff/{owner.User.Id}/role", new ChangeStaffRoleRequest("Manager"));
        reRole.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reRole.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("STAFF_SELF_ACTION");
    }

    // ---- lifecycle ----------------------------------------------------

    [Fact]
    public async Task Changing_a_role_takes_effect_after_the_member_signs_in_again()
    {
        await RegisterLoginAndAuthorizeAsync();
        var (cashier, cashierId) = await AddStaffAsync("promote@example.com", UserRole.Cashier);

        // Owner promotes the Cashier to Manager
        var changed = await Client.PutAsJsonAsync($"/api/staff/{cashierId}/role", new ChangeStaffRoleRequest("Manager"));
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<StaffMemberDto>(TestJson.Options))!.Role.Should().Be(UserRole.Manager);

        // Both databases are updated
        (await InPlatformScopeAsync(db => db.PlatformUserLogins.SingleAsync(l => l.Id == cashierId))).Role.Should().Be(UserRole.Manager);
        (await InScopeAsync(db => db.Users.SingleAsync(u => u.Id == cashierId))).Role.Should().Be(UserRole.Manager);

        // The Cashier's old token no longer works — the role in it is stale
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier.AccessToken);
        (await Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // After signing in again the new role is in effect (Manager can view sales)
        ClearAuth();
        var refreshed = await LoginAsync("promote@example.com");
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        (await Client.GetAsync("/api/sales")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deactivating_a_member_stops_access_immediately_and_preserves_history()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var (cashier, cashierId) = await AddStaffAsync("bye@example.com", UserRole.Cashier);

        // The Owner sets up the catalog + register; the Cashier then rings up a sale.
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id);

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier.AccessToken);
        var session = await OpenSessionAsync(register.Id);
        var sale = await CheckoutOkAsync(new Negosio.Application.Pos.CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new Negosio.Application.Pos.CheckoutItemInput(variantId, 1m, null) },
            new[] { new Negosio.Application.Pos.CheckoutPaymentInput(Negosio.Domain.Enums.PaymentMethod.Cash, ReceivedAmount: 200m) }));

        // Owner deactivates the cashier.
        Authorize(owner.AccessToken);
        (await Client.PostAsync($"/api/staff/{cashierId}/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The cashier's still-unexpired token is now rejected.
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier.AccessToken);
        (await Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // And they cannot sign in again.
        ClearAuth();
        var relogin = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest("bye@example.com", "SecurePassword123!"));
        relogin.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await relogin.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("ACCOUNT_INACTIVE");

        // History still references the (now deactivated) user.
        await InTenantScopeAsync(owner.User.TenantId, async db =>
        {
            var persisted = await db.Sales.SingleAsync(s => s.Id == sale.SaleId);
            persisted.CreatedByUserId.Should().Be(cashierId);
            var deactivated = await db.Users.SingleAsync(u => u.Id == cashierId);
            deactivated.IsActive.Should().BeFalse();
            return true;
        });

        // Owner reactivates — sign-in works again.
        Authorize(owner.AccessToken);
        (await Client.PostAsync($"/api/staff/{cashierId}/reactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        ClearAuth();
        (await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest("bye@example.com", "SecurePassword123!")))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Deactivated_Cashier_cannot_obtain_cost_prices()
    {
        // A restricted role must never receive costPrice. Guard against a Staff-phase regression.
        var owner = await RegisterLoginAndAuthorizeAsync();
        var (cashier, _) = await AddStaffAsync("noc@example.com", UserRole.Cashier);
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id, "Widget", sku: "W-1", costPrice: 40m, sellingPrice: 75m);

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier.AccessToken);
        var detail = await Client.GetFromJsonAsync<Negosio.Application.Catalog.ProductDetailDto>(
            $"/api/products/{product.Product.Id}", TestJson.Options);

        detail!.Variants.Single().CostPrice.Should().BeNull();
    }

    // ---- tenant isolation --------------------------------------------

    [Fact]
    public async Task A_tenant_cannot_see_or_touch_another_tenants_staff()
    {
        var tenantA = await RegisterLoginAndAuthorizeAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        var (_, staffAId) = await AddStaffAsync("staffa@example.com", UserRole.Cashier);
        var inviteA = await InviteAsync("penda@example.com", UserRole.Viewer);

        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "B Co", email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);

        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        list!.Should().OnlyContain(m => m.Email == "b@example.com");

        (await Client.PutAsJsonAsync($"/api/staff/{staffAId}/role", new ChangeStaffRoleRequest("Manager")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PostAsync($"/api/staff/{staffAId}/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.DeleteAsync($"/api/staff/invitations/{inviteA.InvitationId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
