using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Pos;

/// <summary>
/// Checkout's whole-sale Delivery path: the cashier picks Delivery once for the whole sale (there is
/// no per-item delivery quantity anymore — <see cref="CheckoutItemInput"/> carries none), and every
/// line on the sale is earmarked for delivery at its full quantity. See
/// <see cref="CheckoutAllocationTests"/> for the TakeNow/Pickup counterparts and
/// <see cref="CheckoutDeliveryChargeTests"/> for delivery-charge-specific behavior.
/// </summary>
public class CheckoutDeliveryFulfillmentTests : IntegrationTest
{
    public CheckoutDeliveryFulfillmentTests(NegosioApiFactory factory) : base(factory)
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
    public async Task TakeNow_checkout_leaves_every_item_at_zero_delivery_and_pickup_quantity()
    {
        var scene = await ArrangeAsync();

        // Method omitted entirely -> defaults to TakeNow, matching an older client that never sends it.
        var request = new CheckoutRequest(
            scene.BranchId, scene.SessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(scene.VariantId, 5m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) });

        var result = await CheckoutOkAsync(request);

        result.Items.Should().ContainSingle();
        result.Items[0].DeliveryRequiredQuantity.Should().Be(0m);

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync(i => i.Id == result.Items[0].SaleItemId);
            item.DeliveryRequiredQuantity.Should().Be(0m);
            item.PickupRequiredQuantity.Should().Be(0m);
            item.TakeNowQuantity.Should().Be(5m);
            return true;
        });
    }

    [Fact]
    public async Task Delivery_checkout_sets_every_item_delivery_required_quantity_to_its_full_quantity()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantAId) = await SeedStockedProductAsync(branchId, category.Id, name: "Coke 1.5L", sku: "SKU-DEL-A", sellingPrice: 100m, openingStock: 50m);
        var (_, variantBId) = await SeedStockedProductAsync(branchId, category.Id, name: "Sprite 1.5L", sku: "SKU-DEL-B", sellingPrice: 50m, openingStock: 50m);

        var request = new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[]
            {
                new CheckoutItemInput(variantAId, 3m, null),
                new CheckoutItemInput(variantBId, 7m, null),
            },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) },
            Method: FulfillmentMethod.Delivery);

        var result = await CheckoutOkAsync(request);

        result.Items.Should().HaveCount(2);

        await InScopeAsync(async db =>
        {
            var items = await db.SaleItems.Where(i => i.SaleId == result.SaleId).ToListAsync();
            items.Should().HaveCount(2);
            // Both lines — not just the first one — must be fully earmarked for delivery.
            items.Should().OnlyContain(i => i.DeliveryRequiredQuantity == i.Quantity);
            items.Should().OnlyContain(i => i.PickupRequiredQuantity == 0m);
            items.Should().OnlyContain(i => i.TakeNowQuantity == 0m);
            return true;
        });
    }

    [Fact]
    public async Task Replaying_the_same_client_request_returns_the_same_created_SaleItem_identifiers()
    {
        var scene = await ArrangeAsync();
        var request = Sale(scene, quantity: 3m, method: FulfillmentMethod.Delivery, cashReceived: 300m);

        var first = await CheckoutOkAsync(request);
        var second = await CheckoutOkAsync(request);

        second.Items[0].SaleItemId.Should().Be(first.Items[0].SaleItemId);
    }
}
