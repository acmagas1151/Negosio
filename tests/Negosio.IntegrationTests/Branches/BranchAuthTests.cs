using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Branches;

public class BranchAuthTests : IntegrationTest
{
    private const string Pw = "SecurePassword123!";

    public BranchAuthTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task A_cashier_with_an_active_branch_can_log_in()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        await AddLoginableTenantUserAsync("cara@example.com", Pw, UserRole.Cashier, bgc.Id);

        var login = await RawLoginAsync("cara@example.com", Pw);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.InventoryStaff)]
    public async Task A_branch_scoped_user_whose_branch_is_inactive_cannot_log_in(UserRole role)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        await AddLoginableTenantUserAsync("scoped@example.com", Pw, role, bgc.Id);

        Authorize(owner.AccessToken);
        (await Client.PostAsync($"/api/branches/{bgc.Id}/deactivate", null)).EnsureSuccessStatusCode();

        var login = await RawLoginAsync("scoped@example.com", Pw);

        login.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await login.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("BRANCH_INACTIVE");
    }

    [Fact]
    public async Task Owner_and_Admin_log_in_despite_inactive_branches()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        await AddLoginableTenantUserAsync("admin@example.com", Pw, UserRole.Admin);
        (await Client.PostAsync($"/api/branches/{bgc.Id}/deactivate", null)).EnsureSuccessStatusCode();

        (await RawLoginAsync("owner@example.com", Pw)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RawLoginAsync("admin@example.com", Pw)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_already_issued_token_stops_working_when_the_branch_is_deactivated()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        await AddLoginableTenantUserAsync("cara@example.com", Pw, UserRole.Cashier, bgc.Id);

        var caraToken = (await (await RawLoginAsync("cara@example.com", Pw)).Content
            .ReadFromJsonAsync<LoginResponseBody>(TestJson.Options))!.AccessToken;

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", caraToken);
        (await Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(owner.AccessToken);
        (await Client.PostAsync($"/api/branches/{bgc.Id}/deactivate", null)).EnsureSuccessStatusCode();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", caraToken);
        var stale = await Client.GetAsync("/api/dashboard");
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await stale.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("BRANCH_INACTIVE");
    }

    [Fact]
    public async Task Reactivating_the_branch_restores_login()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        await AddLoginableTenantUserAsync("cara@example.com", Pw, UserRole.Cashier, bgc.Id);
        (await Client.PostAsync($"/api/branches/{bgc.Id}/deactivate", null)).EnsureSuccessStatusCode();

        (await RawLoginAsync("cara@example.com", Pw)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Client.PostAsync($"/api/branches/{bgc.Id}/reactivate", null)).EnsureSuccessStatusCode();

        (await RawLoginAsync("cara@example.com", Pw)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record LoginResponseBody(string AccessToken);
}
