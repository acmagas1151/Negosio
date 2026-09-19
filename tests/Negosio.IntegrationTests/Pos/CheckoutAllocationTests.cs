using System.Net;
using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Pos;

/// <summary>
/// Checkout's whole-sale fulfillment choice: the cashier picks exactly one of Take now / Delivery /
/// Pickup, and it applies to 100% of every sale item — there is no per-item allocation anymore.
/// <see cref="CheckoutDeliveryFulfillmentTests"/> covers the TakeNow-default and Delivery cases;
/// this file covers Pickup and the request-level validation around <see cref="CheckoutRequest.Method"/>.
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

    private static CheckoutRequest Sale(Scene s, decimal quantity, FulfillmentMethod method, decimal cashReceived) => new(
        s.BranchId, s.SessionId, Guid.NewGuid(),
        new[] { new CheckoutItemInput(s.VariantId, quantity, null) },
        new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) },
        Method: method);

    [Fact]
    public async Task Pickup_checkout_sets_every_item_pickup_required_quantity_to_its_full_quantity()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantAId) = await SeedStockedProductAsync(branchId, category.Id, name: "Coke 1.5L", sku: "SKU-PKP-A", sellingPrice: 100m, openingStock: 50m);
        var (_, variantBId) = await SeedStockedProductAsync(branchId, category.Id, name: "Sprite 1.5L", sku: "SKU-PKP-B", sellingPrice: 50m, openingStock: 50m);

        var request = new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[]
            {
                new CheckoutItemInput(variantAId, 2m, null),
                new CheckoutItemInput(variantBId, 6m, null),
            },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) },
            Method: FulfillmentMethod.Pickup);

        var result = await CheckoutOkAsync(request);

        result.Items.Should().HaveCount(2);

        await InScopeAsync(async db =>
        {
            var items = await db.SaleItems.Where(i => i.SaleId == result.SaleId).ToListAsync();
            items.Should().HaveCount(2);
            // Both lines — not just the first one — must be fully earmarked for pickup.
            items.Should().OnlyContain(i => i.PickupRequiredQuantity == i.Quantity);
            items.Should().OnlyContain(i => i.DeliveryRequiredQuantity == 0m);
            items.Should().OnlyContain(i => i.TakeNowQuantity == 0m);
            return true;
        });
    }

    [Fact]
    public async Task An_out_of_range_fulfillment_method_value_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(Sale(scene, quantity: 1m, method: (FulfillmentMethod)999, cashReceived: 200m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
