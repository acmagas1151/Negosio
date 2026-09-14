using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Pos;

/// <summary>
/// Checkout is where the spec's core identity — Sold = TakeNow + Delivery-intended + Pickup-intended
/// — gets enforced at its source. These tests cover the three-way allocation added in Task 9, on top
/// of the delivery-only allocation already covered by <see cref="CheckoutDeliveryFulfillmentTests"/>
/// and the delivery-charge behavior covered by <see cref="CheckoutDeliveryChargeTests"/> (both of
/// which must keep passing untouched).
/// </summary>
public class CheckoutAllocationTests : IntegrationTest
{
    public CheckoutAllocationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record Scene(Guid BranchId, Guid SessionId, Guid VariantId);

    private async Task<Scene> ArrangeAsync(decimal price = 100m, decimal stock = 50m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: stock);
        return new Scene(branchId, session.Id, variantId);
    }

    private static CheckoutRequest Sale(
        Scene s, decimal quantity, decimal deliveryRequiredQuantity, decimal pickupRequiredQuantity,
        decimal cashReceived, decimal deliveryCharge = 0m) => new(
        s.BranchId, s.SessionId, Guid.NewGuid(),
        new[] { new CheckoutItemInput(s.VariantId, quantity, null, deliveryRequiredQuantity, pickupRequiredQuantity) },
        new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) },
        DeliveryCharge: deliveryCharge);

    private Task<SaleFulfillmentSummaryDto?> GetSummaryAsync(Guid saleId) =>
        Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>($"/api/sales/{saleId}/fulfillment", TestJson.Options);

    [Fact]
    public async Task A_mixed_allocation_splits_take_now_delivery_and_pickup_correctly()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 3m, cashReceived: 1000m));

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync(i => i.Id == result.Items[0].SaleItemId);
            item.DeliveryRequiredQuantity.Should().Be(4m);
            item.PickupRequiredQuantity.Should().Be(3m);
            item.TakeNowQuantity.Should().Be(3m);
            return true;
        });
    }

    [Fact]
    public async Task Pure_delivery_allocation_still_works_as_a_regression_guard()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 5m, pickupRequiredQuantity: 0m, cashReceived: 500m));

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync(i => i.Id == result.Items[0].SaleItemId);
            item.TakeNowQuantity.Should().Be(0m);
            return true;
        });
    }

    [Fact]
    public async Task Pure_pickup_allocation_zeroes_take_now_and_gates_the_fulfillment_summary_to_pickup_only()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 5m, cashReceived: 500m));

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync(i => i.Id == result.Items[0].SaleItemId);
            item.TakeNowQuantity.Should().Be(0m);
            return true;
        });

        var summary = await GetSummaryAsync(result.SaleId);
        summary!.CanCreatePickup.Should().BeTrue();
        summary.CanCreateDelivery.Should().BeFalse();
    }

    [Fact]
    public async Task No_delivery_or_pickup_intent_leaves_the_whole_line_take_now_and_fulfillment_not_applicable()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 0m, cashReceived: 500m));

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync(i => i.Id == result.Items[0].SaleItemId);
            item.TakeNowQuantity.Should().Be(5m);
            return true;
        });

        var summary = await GetSummaryAsync(result.SaleId);
        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.NotApplicable);
    }

    [Fact]
    public async Task Delivery_plus_pickup_exceeding_the_item_quantity_is_rejected_and_creates_no_sale()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 3m, pickupRequiredQuantity: 3m, cashReceived: 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Delivery and pickup quantities together cannot exceed the item quantity.");

        await InScopeAsync(async db =>
        {
            (await db.Sales.CountAsync()).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Omitting_pickupRequiredQuantity_from_the_JSON_body_behaves_as_zero()
    {
        var scene = await ArrangeAsync();

        // Simulates an older POS client that has never heard of pickupRequiredQuantity — the key is
        // absent from the body entirely, not just set to 0.
        var legacyBody = new
        {
            branchId = scene.BranchId,
            registerSessionId = scene.SessionId,
            clientRequestId = Guid.NewGuid(),
            items = new[]
            {
                new
                {
                    productVariantId = scene.VariantId,
                    quantity = 5m,
                    deliveryRequiredQuantity = 2m,
                },
            },
            payments = new[]
            {
                new { method = "Cash", receivedAmount = 500m },
            },
        };

        var response = await Client.PostAsJsonAsync("/api/pos/checkout", legacyBody, TestJson.Options);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync(i => i.Id == result.Items[0].SaleItemId);
            item.PickupRequiredQuantity.Should().Be(0m);
            item.DeliveryRequiredQuantity.Should().Be(2m);
            item.TakeNowQuantity.Should().Be(3m);
            return true;
        });
    }

    [Fact]
    public async Task Negative_pickupRequiredQuantity_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: -1m, cashReceived: 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_mixed_delivery_and_pickup_checkout_still_stores_the_delivery_charge_exactly_once()
    {
        var scene = await ArrangeAsync(price: 100m);

        var result = await CheckoutOkAsync(Sale(scene, quantity: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 3m, cashReceived: 1100m, deliveryCharge: 100m));

        result.DeliveryCharge.Should().Be(100m);
        result.GrandTotal.Should().Be(1100m);

        await InScopeAsync(async db =>
        {
            var sale = await db.Sales.SingleAsync();
            sale.DeliveryCharge.Should().Be(100m);
            return true;
        });
    }
}
