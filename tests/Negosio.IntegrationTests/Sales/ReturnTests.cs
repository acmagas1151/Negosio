using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class ReturnTests : IntegrationTest
{
    public ReturnTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record Scene(Guid BranchId, Guid VariantId, Guid SaleId, Guid SaleItemId, decimal Price);

    private async Task<Scene> SellAsync(decimal price = 75m, decimal stock = 20m, decimal quantity = 2m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: stock);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, quantity, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: price * quantity + 100m) }));

        var detail = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);
        return new Scene(branchId, variantId, sale.SaleId, detail!.Items.Single().Id, price);
    }

    [Fact]
    public async Task Full_return_restores_inventory_and_marks_the_sale_refunded()
    {
        var scene = await SellAsync(price: 75m, stock: 20m, quantity: 2m);

        var response = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(
                new[] { new ReturnLineInput(scene.SaleItemId, 2m, Restock: true) },
                "Customer changed their mind",
                PaymentMethod.Cash,
                null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<SaleReturnDto>(TestJson.Options))!;
        body.ReturnNumber.Should().MatchRegex(@"^\d{7}$");
        body.OriginalSaleNumber.Should().MatchRegex(@"^\d{7}$");
        int.Parse(body.ReturnNumber).Should().BeGreaterThan(int.Parse(body.OriginalSaleNumber));
        body.TotalRefund.Should().Be(150m);

        await InScopeAsync(async db =>
        {
            var sale = await db.Sales.SingleAsync(s => s.Id == scene.SaleId);
            sale.Status.Should().Be(SaleStatus.Refunded);

            var inv = await db.BranchInventories.SingleAsync(i => i.ProductVariantId == scene.VariantId);
            inv.QuantityOnHand.Should().Be(20m); // 20 - 2 sold + 2 returned

            var movement = await db.StockMovements.SingleAsync(m => m.Type == StockMovementType.Return);
            movement.Quantity.Should().Be(2m);
            movement.ReferenceType.Should().Be("Return");
            return true;
        });
    }

    [Fact]
    public async Task Partial_returns_accumulate_and_cannot_exceed_the_purchased_quantity()
    {
        var scene = await SellAsync(price: 50m, stock: 10m, quantity: 3m);

        var first = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "One back", PaymentMethod.Cash, null));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "Another back", PaymentMethod.Cash, null));
        second.StatusCode.Should().Be(HttpStatusCode.Created);

        // Only 1 remains returnable; asking for 2 must fail.
        var third = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 2m) }, "Too many", PaymentMethod.Cash, null));
        third.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await third.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("RETURN_QUANTITY_EXCEEDED");

        await InScopeAsync(async db =>
        {
            var sale = await db.Sales.SingleAsync(s => s.Id == scene.SaleId);
            sale.Status.Should().Be(SaleStatus.PartiallyRefunded);
            var inv = await db.BranchInventories.SingleAsync(i => i.ProductVariantId == scene.VariantId);
            inv.QuantityOnHand.Should().Be(9m); // 10 - 3 + 2 restored
            return true;
        });
    }

    [Fact]
    public async Task Sales_and_returns_share_one_atomic_branch_transaction_sequence()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 50m, openingStock: 20m);

        async Task<(Guid SaleId, string Number, Guid ItemId)> SellOneAsync()
        {
            var sale = await CheckoutOkAsync(new CheckoutRequest(
                branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 1m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 100m) }));
            var detail = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);
            return (sale.SaleId, sale.SaleNumber, detail!.Items.Single().Id);
        }

        async Task<string> ReturnOneAsync(Guid saleId, Guid saleItemId, string expectedOriginalSaleNumber)
        {
            var response = await Client.PostAsJsonAsync(
                $"/api/sales/{saleId}/returns",
                new CreateReturnRequest(
                    new[] { new ReturnLineInput(saleItemId, 1m, Restock: true) },
                    "interleave check", PaymentMethod.Cash, null));
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            var body = (await response.Content.ReadFromJsonAsync<SaleReturnDto>(TestJson.Options))!;
            body.OriginalSaleNumber.Should().Be(expectedOriginalSaleNumber);
            return body.ReturnNumber;
        }

        var a = await SellOneAsync();
        var b = await SellOneAsync();
        var r1 = await ReturnOneAsync(a.SaleId, a.ItemId, a.Number);
        var c = await SellOneAsync();
        var r2 = await ReturnOneAsync(b.SaleId, b.ItemId, b.Number);

        a.Number.Should().Be("0000001");
        b.Number.Should().Be("0000002");
        r1.Should().Be("0000003");
        c.Number.Should().Be("0000004");
        r2.Should().Be("0000005");
    }

    [Fact]
    public async Task Tenant_A_cannot_return_tenant_B_sale()
    {
        var scene = await SellAsync();

        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "Nope", PaymentMethod.Cash, null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Return_without_a_reason_is_rejected()
    {
        var scene = await SellAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "", PaymentMethod.Cash, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("VALIDATION_FAILED");
    }

    // ---- Cashier authorization: direct SalesReturn grant, or Manager/Admin/Owner approval ----
    // Same shape as VoidSaleTests' equivalent cases — RefundManage now admits every POS role at the
    // controller, with ReturnAuthorizationResolver (service-level, the actual security boundary)
    // requiring Owner/Admin/Manager, a direct grant, or a verified approval for a Cashier.

    [Fact]
    public async Task Cashier_without_grant_is_rejected_then_succeeds_with_manager_approval()
    {
        var scene = await SellAsync(); // Client is left authorized as Owner.
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        var managerId = await CreateManagerAsync("mgr@example.com", "Manager123!", scene.BranchId);

        Authorize(cashierToken);
        var denied = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "No grant yet", PaymentMethod.Cash, null));
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await denied.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.ReturnApprovalRequired);

        var approved = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(
                new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "Approved by manager", PaymentMethod.Cash, null,
                new VoidSaleApprovalInput("mgr@example.com", "Manager123!")));
        approved.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await approved.Content.ReadFromJsonAsync<SaleReturnDto>(TestJson.Options))!;
        body.CreatedByUserId.Should().Be(await GetUserIdFromTokenAsync(cashierToken));
        body.ApprovedByUserId.Should().Be(managerId, "the manager's approval was required and used");

        await InScopeAsync(async db =>
        {
            var saleReturn = await db.SaleReturns.SingleAsync(r => r.SaleId == scene.SaleId);
            saleReturn.CreatedByUserId.Should().Be(await GetUserIdFromTokenAsync(cashierToken));
            saleReturn.ApprovedByUserId.Should().Be(managerId);
            return true;
        });
    }

    [Fact]
    public async Task Wrong_manager_password_is_rejected_and_the_sale_is_unchanged()
    {
        var scene = await SellAsync();
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        await CreateManagerAsync("mgr@example.com", "Manager123!", scene.BranchId);

        Authorize(cashierToken);
        var res = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(
                new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "Bad password", PaymentMethod.Cash, null,
                new VoidSaleApprovalInput("mgr@example.com", "WrongPassword!")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.InvalidApproverCredentials);

        await InScopeAsync(async db =>
        {
            (await db.SaleReturns.AnyAsync(r => r.SaleId == scene.SaleId)).Should().BeFalse();
            var sale = await db.Sales.SingleAsync(s => s.Id == scene.SaleId);
            sale.Status.Should().Be(SaleStatus.Completed);
            return true;
        });
    }

    [Fact]
    public async Task Cashier_with_a_direct_grant_returns_without_any_approval()
    {
        var scene = await SellAsync(); // Client is left authorized as Owner.
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/staff/{cashierId}/permissions")
        {
            Content = JsonContent.Create(new ChangeStaffPermissionsRequest(SalesVoid: false, SalesReturn: true, DiscountApply: false, CashDrawerOpen: false)),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(cashierToken);
        var res = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "Direct via grant", PaymentMethod.Cash, null));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await res.Content.ReadFromJsonAsync<SaleReturnDto>(TestJson.Options))!;
        body.ApprovedByUserId.Should().BeNull("the cashier acted on their own direct grant — no approval was used");
    }

    /// <summary>Regression: revoking the grant must bring the approval requirement straight back.</summary>
    [Fact]
    public async Task Revoking_the_grant_brings_the_approval_requirement_back()
    {
        var scene = await SellAsync();
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        var grantRequest = new ChangeStaffPermissionsRequest(SalesVoid: false, SalesReturn: true, DiscountApply: false, CashDrawerOpen: false);
        (await Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/staff/{cashierId}/permissions") { Content = JsonContent.Create(grantRequest) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var revokeRequest = new ChangeStaffPermissionsRequest(SalesVoid: false, SalesReturn: false, DiscountApply: false, CashDrawerOpen: false);
        (await Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/staff/{cashierId}/permissions") { Content = JsonContent.Create(revokeRequest) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(cashierToken);
        var res = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(scene.SaleItemId, 1m) }, "Revoked", PaymentMethod.Cash, null));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.ReturnApprovalRequired);
    }
}
