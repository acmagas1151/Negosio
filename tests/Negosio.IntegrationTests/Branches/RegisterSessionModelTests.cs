using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Branches;

public class RegisterSessionModelTests : IntegrationTest
{
    public RegisterSessionModelTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<HttpResponseMessage> OpenAsync(Guid registerId, decimal cash = 1000m) =>
        await Client.PostAsJsonAsync("/api/register-sessions/open", new OpenRegisterSessionRequest(registerId, cash));

    [Fact]
    public async Task One_open_session_per_register()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");

        var cashierA = await AddTenantUserTokenAsync("a@example.com", UserRole.Cashier, branchId);
        var cashierB = await AddTenantUserTokenAsync("b@example.com", UserRole.Cashier, branchId);

        Authorize(cashierA);
        (await OpenAsync(register.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(cashierB);
        var second = await OpenAsync(register.Id);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("REGISTER_SESSION_ALREADY_OPEN");
    }

    [Fact]
    public async Task One_open_session_per_user()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var r1 = await CreateRegisterAsync(branchId, "R1", "R1");
        var r2 = await CreateRegisterAsync(branchId, "R2", "R2");

        var cashier = await AddTenantUserTokenAsync("a@example.com", UserRole.Cashier, branchId);
        Authorize(cashier);

        (await OpenAsync(r1.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await OpenAsync(r2.Id);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("CASHIER_SESSION_OPEN");
    }

    [Fact]
    public async Task Checkout_through_another_users_session_is_forbidden()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id);

        var cashierA = await AddTenantUserTokenAsync("a@example.com", UserRole.Cashier, branchId);
        Authorize(cashierA);
        var session = (await (await OpenAsync(register.Id)).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var cashierB = await AddTenantUserTokenAsync("b@example.com", UserRole.Cashier, branchId);
        Authorize(cashierB);
        var checkout = await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));

        checkout.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await checkout.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("SESSION_NOT_OWNED");
    }

    [Fact]
    public async Task Closing_another_users_session_is_forbidden()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");

        var cashierA = await AddTenantUserTokenAsync("a@example.com", UserRole.Cashier, branchId);
        Authorize(cashierA);
        var session = (await (await OpenAsync(register.Id)).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var cashierB = await AddTenantUserTokenAsync("b@example.com", UserRole.Cashier, branchId);
        Authorize(cashierB);
        var close = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/close", new CloseRegisterSessionRequest(1000m));

        close.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await close.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("SESSION_NOT_OWNED");
    }

    [Fact]
    public async Task Force_close_is_owner_admin_only_and_preserves_ownership()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");

        var caraToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(caraToken);
        var caraSession = (await (await OpenAsync(register.Id, 500m)).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        // Manager cannot force-close
        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, branchId);
        Authorize(managerToken);
        (await Client.PostAsJsonAsync($"/api/register-sessions/{caraSession.Id}/force-close", new CloseRegisterSessionRequest(500m)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Owner can, reconciliation runs, ownership preserved
        Authorize(owner.AccessToken);
        var forced = await Client.PostAsJsonAsync($"/api/register-sessions/{caraSession.Id}/force-close", new CloseRegisterSessionRequest(500m));
        forced.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await forced.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
        dto.Status.Should().Be(RegisterSessionStatus.Closed);
        dto.OpenedByUserId.Should().Be(caraSession.OpenedByUserId); // still the original cashier
        dto.CashDifference.Should().Be(0m); // 500 opening, no sales, 500 counted
    }

    [Fact]
    public async Task Force_close_works_on_a_deactivated_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var register = await CreateRegisterAsync(bgc.Id, "BR", "BR");

        var caraToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);
        Authorize(caraToken);
        var session = (await (await OpenAsync(register.Id, 300m)).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        Authorize(owner.AccessToken);
        (await Client.PostAsync($"/api/branches/{bgc.Id}/deactivate", null)).EnsureSuccessStatusCode();

        var forced = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/force-close", new CloseRegisterSessionRequest(300m));
        forced.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
