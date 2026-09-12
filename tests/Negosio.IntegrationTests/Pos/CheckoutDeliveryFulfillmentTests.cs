using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Pos;

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

    private static CheckoutRequest Sale(Scene s, decimal quantity, decimal deliveryRequiredQuantity, decimal cashReceived) => new(
        s.BranchId, s.SessionId, Guid.NewGuid(),
        new[] { new CheckoutItemInput(s.VariantId, quantity, null, deliveryRequiredQuantity) },
        new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) });

    [Fact]
    public async Task A_line_with_no_delivery_requirement_defaults_the_whole_quantity_to_take_now()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 0m, cashReceived: 500m));

        result.Items.Should().ContainSingle();
        result.Items[0].Quantity.Should().Be(5m);
        result.Items[0].DeliveryRequiredQuantity.Should().Be(0m);

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync();
            item.DeliveryRequiredQuantity.Should().Be(0m);
            item.TakeNowQuantity.Should().Be(5m);
            return true;
        });
    }

    [Fact]
    public async Task A_partial_delivery_requirement_splits_take_now_and_delivery_required_and_is_returned_for_scheduling()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 10m, deliveryRequiredQuantity: 6m, cashReceived: 1000m));

        result.Items[0].Quantity.Should().Be(10m);
        result.Items[0].DeliveryRequiredQuantity.Should().Be(6m);
        result.Items[0].SaleItemId.Should().NotBeEmpty();
        result.Items[0].ProductVariantId.Should().Be(scene.VariantId);

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync();
            item.Id.Should().Be(result.Items[0].SaleItemId);
            item.DeliveryRequiredQuantity.Should().Be(6m);
            item.TakeNowQuantity.Should().Be(4m);
            return true;
        });
    }

    [Fact]
    public async Task Delivery_required_quantity_exceeding_the_sold_quantity_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 5.5m, cashReceived: 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Negative_delivery_required_quantity_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: -1m, cashReceived: 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Replaying_the_same_client_request_returns_the_same_created_SaleItem_identifiers()
    {
        var scene = await ArrangeAsync();
        var request = Sale(scene, quantity: 3m, deliveryRequiredQuantity: 2m, cashReceived: 300m);

        var first = await CheckoutOkAsync(request);
        var second = await CheckoutOkAsync(request);

        second.Items[0].SaleItemId.Should().Be(first.Items[0].SaleItemId);
    }
}
