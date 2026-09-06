using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Pos;

public class CheckoutTests : IntegrationTest
{
    public CheckoutTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record Scene(Guid BranchId, Guid SessionId, Guid ProductId, Guid VariantId, decimal Price, decimal Stock);

    private async Task<Scene> ArrangeAsync(decimal price = 75m, decimal stock = 20m, bool trackInventory = true)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();

        if (trackInventory)
        {
            var (productId, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: stock);
            return new Scene(branchId, session.Id, productId, variantId, price, stock);
        }

        var product = await CreateSimpleProductAsync(category.Id, "Service Fee", sku: "SVC-1", sellingPrice: price, trackInventory: false);
        return new Scene(branchId, session.Id, product.Product.Id, product.Variants.Single().Id, price, 0m);
    }

    private static CheckoutRequest CashSale(Scene s, decimal quantity, decimal cashReceived, CheckoutDiscountInput? discount = null) => new(
        s.BranchId, s.SessionId, Guid.NewGuid(),
        new[] { new CheckoutItemInput(s.VariantId, quantity, discount) },
        new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) });

    [Fact]
    public async Task Simple_cash_sale_persists_sale_items_payment_and_deducts_inventory()
    {
        var scene = await ArrangeAsync(price: 75m, stock: 20m);

        var result = await CheckoutOkAsync(CashSale(scene, quantity: 2m, cashReceived: 200m));

        result.GrandTotal.Should().Be(150m);
        result.ChangeDue.Should().Be(50m);
        result.SaleNumber.Should().MatchRegex(@"^\d{7}$");

        await InScopeAsync(async db =>
        {
            var sale = await db.Sales.Include(x => x.Items).Include(x => x.Payments).SingleAsync();
            sale.Items.Should().ContainSingle();
            sale.Items.Single().ProductNameSnapshot.Should().Be("Coke 1.5L");
            sale.Items.Single().NetAmount.Should().Be(150m);
            sale.Payments.Should().ContainSingle(p => p.Method == PaymentMethod.Cash && p.Amount == 150m);

            var inv = await db.BranchInventories.SingleAsync(i => i.ProductVariantId == scene.VariantId);
            inv.QuantityOnHand.Should().Be(18m);

            var movement = await db.StockMovements.SingleAsync(m => m.Type == StockMovementType.Sale);
            movement.Quantity.Should().Be(-2m);
            movement.QuantityBefore.Should().Be(20m);
            movement.QuantityAfter.Should().Be(18m);
            movement.ReferenceType.Should().Be("Sale");
            movement.ReferenceId.Should().Be(sale.Id);
            return true;
        });
    }

    [Fact]
    public async Task Insufficient_inventory_rejects_checkout_and_persists_nothing()
    {
        var scene = await ArrangeAsync(price: 50m, stock: 1m);

        var response = await CheckoutAsync(CashSale(scene, quantity: 5m, cashReceived: 500m));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INSUFFICIENT_INVENTORY");

        await InScopeAsync(async db =>
        {
            (await db.Sales.CountAsync()).Should().Be(0);
            (await db.SaleItems.CountAsync()).Should().Be(0);
            (await db.Payments.CountAsync()).Should().Be(0);
            (await db.StockMovements.CountAsync(m => m.Type == StockMovementType.Sale)).Should().Be(0);
            (await db.BranchInventories.Where(i => i.ProductVariantId == scene.VariantId).Select(i => i.QuantityOnHand).SingleAsync())
                .Should().Be(1m);
            return true;
        });
    }

    [Fact]
    public async Task Non_tracked_item_sells_without_an_inventory_row()
    {
        var scene = await ArrangeAsync(price: 30m, trackInventory: false);

        var result = await CheckoutOkAsync(CashSale(scene, quantity: 1m, cashReceived: 30m));

        result.GrandTotal.Should().Be(30m);
        await InScopeAsync(async db =>
        {
            (await db.BranchInventories.CountAsync(i => i.ProductVariantId == scene.VariantId)).Should().Be(0);
            (await db.StockMovements.CountAsync()).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Server_price_is_used_even_when_the_client_would_send_something_else()
    {
        // The checkout contract has no unitPrice field; the server always prices from ProductVariant.
        var scene = await ArrangeAsync(price: 75m, stock: 10m);

        var result = await CheckoutOkAsync(CashSale(scene, quantity: 1m, cashReceived: 75m));

        result.Subtotal.Should().Be(75m);
        result.GrandTotal.Should().Be(75m);
    }

    [Fact]
    public async Task Closed_register_session_is_rejected()
    {
        var scene = await ArrangeAsync();
        // Close the session out from under the checkout.
        var sessionId = scene.SessionId;
        await Client.PostAsJsonAsync($"/api/register-sessions/{sessionId}/close", new CloseRegisterSessionRequest(0m));

        var response = await CheckoutAsync(CashSale(scene, 1m, 100m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("REGISTER_SESSION_NOT_OPEN");
    }

    [Fact]
    public async Task Invalid_branch_is_rejected()
    {
        var scene = await ArrangeAsync();
        var request = scene with { };
        var body = new CheckoutRequest(
            Guid.NewGuid(), scene.SessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(scene.VariantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 100m) });

        var response = await CheckoutAsync(body);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Duplicate_variant_lines_are_merged_into_one_quantity()
    {
        var scene = await ArrangeAsync(price: 10m, stock: 10m);

        var body = new CheckoutRequest(
            scene.BranchId, scene.SessionId, Guid.NewGuid(),
            new[]
            {
                new CheckoutItemInput(scene.VariantId, 1m, null),
                new CheckoutItemInput(scene.VariantId, 2m, null),
            },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 100m) });

        var result = await CheckoutOkAsync(body);
        result.GrandTotal.Should().Be(30m);

        await InScopeAsync(async db =>
        {
            var items = await db.SaleItems.ToListAsync();
            items.Should().ContainSingle();
            items.Single().Quantity.Should().Be(3m);
            return true;
        });
    }

    [Fact]
    public async Task Payment_below_the_total_is_rejected()
    {
        var scene = await ArrangeAsync(price: 100m, stock: 5m);

        var response = await CheckoutAsync(CashSale(scene, quantity: 1m, cashReceived: 50m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("PAYMENT_INSUFFICIENT");
    }

    [Fact]
    public async Task Percentage_and_fixed_discounts_are_calculated_by_the_backend()
    {
        var scene = await ArrangeAsync(price: 100m, stock: 10m);

        var pct = await CheckoutOkAsync(CashSale(scene, quantity: 2m, cashReceived: 500m,
            discount: new CheckoutDiscountInput(DiscountType.Percentage, 10m)));
        pct.Subtotal.Should().Be(200m);
        pct.DiscountTotal.Should().Be(20m);
        pct.GrandTotal.Should().Be(180m);

        var fixedDisc = await CheckoutOkAsync(CashSale(scene, quantity: 1m, cashReceived: 500m,
            discount: new CheckoutDiscountInput(DiscountType.FixedAmount, 15m)));
        fixedDisc.DiscountTotal.Should().Be(15m);
        fixedDisc.GrandTotal.Should().Be(85m);
    }

    [Fact]
    public async Task Receipt_is_available_after_checkout()
    {
        var scene = await ArrangeAsync(price: 75m, stock: 10m);
        var sale = await CheckoutOkAsync(CashSale(scene, quantity: 2m, cashReceived: 200m));

        var receipt = await Client.GetFromJsonAsync<ReceiptDto>($"/api/sales/{sale.SaleId}/receipt", TestJson.Options);

        receipt!.SaleNumber.Should().Be(sale.SaleNumber);
        receipt.GrandTotal.Should().Be(150m);
        receipt.Lines.Should().ContainSingle(l => l.Quantity == 2m && l.NetAmount == 150m);
        receipt.ChangeDue.Should().Be(50m);
    }

    /// <summary>
    /// DiscountApply is enforced by CheckoutService.EnsureDiscountAuthorizedAsync (unchanged this
    /// pass), but until now nothing could actually grant it — StaffController's write endpoint was
    /// hardcoded to SalesVoid. This is the first end-to-end proof the generalized
    /// IUserPermissionGrantService.SetAsync write path actually reaches real enforcement: a Cashier
    /// who holds the grant applies a discount directly, with no approval attached.
    /// </summary>
    [Fact]
    public async Task Cashier_with_a_discount_grant_applies_a_discount_directly()
    {
        var scene = await ArrangeAsync(price: 100m, stock: 10m); // Client is left authorized as Owner.
        // A Cashier needs their own register session (CheckoutService requires session ownership) —
        // create their register now, while still authorized as Owner.
        var cashierRegister = await CreateRegisterAsync(scene.BranchId, "R-Cashier", "R-CASH");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/staff/{cashierId}/permissions")
        {
            Content = JsonContent.Create(new Negosio.Application.Staff.ChangeStaffPermissionsRequest(
                SalesVoid: false, SalesReturn: false, DiscountApply: true, CashDrawerOpen: false)),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(cashierToken);
        var session = await OpenSessionAsync(cashierRegister.Id);
        var request = new CheckoutRequest(
            scene.BranchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(scene.VariantId, 1m, new CheckoutDiscountInput(DiscountType.Percentage, 10m)) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 100m) });

        var result = await CheckoutOkAsync(request);
        result.DiscountTotal.Should().Be(10m);
    }
}
