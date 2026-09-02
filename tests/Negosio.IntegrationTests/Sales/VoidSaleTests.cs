using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class VoidSaleTests : IntegrationTest
{
    public VoidSaleTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<(Guid SaleId, Guid SessionId, Guid VariantId, Guid BranchId)> CheckoutOneSaleAsync(
        string cashierEmail, Guid branchId, Guid registerId)
    {
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, openingStock: 10m);

        var cashierToken = await AddTenantUserTokenAsync(cashierEmail, UserRole.Cashier, branchId);
        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(registerId, 1000m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkout = await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 3m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 300m) }));
        var result = (await checkout.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        return (result.SaleId, session.Id, variantId, branchId);
    }

    private static HttpRequestMessage VoidRequest(Guid saleId, string reason, VoidSaleApprovalInput? approval = null) =>
        new(HttpMethod.Post, $"/api/sales/{saleId}/void") { Content = JsonContent.Create(new VoidSaleRequest(reason, approval)) };

    [Fact]
    public async Task Owner_voids_directly_and_restores_inventory()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, variantId, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        var before = await GetInventoryQuantityAsync(branchId, variantId);
        var res = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await res.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.Sale.Status.Should().Be(SaleStatus.Voided);
        detail.Sale.SaleNumber.Should().Be("0000001"); // unchanged
        detail.VoidedByUserId.Should().NotBeNull();
        detail.ApprovedByUserId.Should().BeNull();

        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(before + 3m);
    }

    [Fact]
    public async Task Manager_voids_own_branch_but_not_another_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);
        var mainRegister = await CreateRegisterAsync(main, "M1", "M1");
        var (mainSaleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", main, mainRegister.Id);

        var bgcManagerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, bgc.Id);
        Authorize(bgcManagerToken);
        var res = await Client.SendAsync(VoidRequest(mainSaleId, "Wrong branch attempt"));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cashier_with_grant_voids_directly_with_reason_only()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);
        var cashierId = await GetUserIdFromTokenAsync(await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId));

        Authorize(owner.AccessToken);
        await Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/staff/{cashierId}/permissions")
        {
            Content = JsonContent.Create(new ChangeStaffPermissionsRequest(true))
        });

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Duplicate transaction"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await res.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.ApprovedByUserId.Should().BeNull();
        detail.VoidedByUserId.Should().Be(cashierId);
    }

    [Fact]
    public async Task Cashier_without_grant_requires_approval_then_succeeds_with_valid_manager_credentials()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        var managerId = await CreateManagerAsync("mgr@example.com", "Manager123!", branchId);

        Authorize(cashierToken);
        var denied = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method"));
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await denied.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidApprovalRequired);

        var approved = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method",
            new VoidSaleApprovalInput("mgr@example.com", "Manager123!")));

        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await approved.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.VoidedByUserId.Should().Be(cashierId);
        detail.ApprovedByUserId.Should().Be(managerId);
    }

    [Fact]
    public async Task Wrong_manager_password_is_rejected_and_nothing_changes()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, variantId, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        await CreateManagerAsync("mgr@example.com", "Manager123!", branchId);

        Authorize(cashierToken);
        var before = await GetInventoryQuantityAsync(branchId, variantId);
        var res = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method",
            new VoidSaleApprovalInput("mgr@example.com", "WrongPassword!")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.InvalidApproverCredentials);
        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(before);
    }

    [Fact]
    public async Task Manager_from_a_different_branch_cannot_approve()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(main, "M1", "M1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", main, register.Id);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, main);
        await CreateManagerAsync("bgc.mgr@example.com", "Manager123!", bgc.Id);

        Authorize(cashierToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method",
            new VoidSaleApprovalInput("bgc.mgr@example.com", "Manager123!")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidApproverWrongBranch);
    }

    [Fact]
    public async Task Already_voided_sale_cannot_be_voided_again()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, variantId, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        await Client.SendAsync(VoidRequest(saleId, "First void"));
        var afterFirst = await GetInventoryQuantityAsync(branchId, variantId);

        var second = await Client.SendAsync(VoidRequest(saleId, "Second attempt"));

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.SaleNotVoidable);
        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(afterFirst); // not restored twice
    }

    [Fact]
    public async Task Sale_with_a_return_cannot_be_voided()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        var items = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{saleId}", TestJson.Options);
        var firstItemId = items!.Items[0].Id;
        await Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new CreateReturnRequest(
            new[] { new ReturnLineInput(firstItemId, 1m, true) }, "Customer changed mind", PaymentMethod.Cash, null));

        var res = await Client.SendAsync(VoidRequest(saleId, "Attempt after return"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.SaleHasReturns);
    }

    [Fact]
    public async Task Voided_sale_cannot_later_be_returned_against()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        var items = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{saleId}", TestJson.Options);
        var firstItemId = items!.Items[0].Id;
        await Client.SendAsync(VoidRequest(saleId, "Wrong payment method"));

        var res = await Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new CreateReturnRequest(
            new[] { new ReturnLineInput(firstItemId, 1m, true) }, "Too late", PaymentMethod.Cash, null));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.ReturnNotAllowed);
    }

    [Fact]
    public async Task Voiding_a_sale_whose_session_is_already_closed_is_rejected()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, sessionId, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);
        await Client.PostAsJsonAsync($"/api/register-sessions/{sessionId}/close", new CloseRegisterSessionRequest(1300m));

        Authorize(owner.AccessToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Too late"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidSessionClosed);
    }

    [Fact]
    public async Task Voiding_a_sale_from_a_prior_day_is_rejected()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        await BackdateSaleCompletedAtAsync(saleId, DateTime.UtcNow.AddDays(-1));

        Authorize(owner.AccessToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Too late"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidCutoffExpired);
    }
}
