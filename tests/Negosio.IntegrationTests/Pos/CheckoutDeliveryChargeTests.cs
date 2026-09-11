using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Pos;

public class CheckoutDeliveryChargeTests : IntegrationTest
{
    public CheckoutDeliveryChargeTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record Scene(Guid BranchId, Guid SessionId, Guid VariantId);

    private async Task<Scene> ArrangeAsync(decimal price = 100m, decimal stock = 20m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: stock);
        return new Scene(branchId, session.Id, variantId);
    }

    private static CheckoutRequest DeliverySale(Scene s, decimal quantity, decimal cashReceived, decimal deliveryCharge) => new(
        s.BranchId, s.SessionId, Guid.NewGuid(),
        new[] { new CheckoutItemInput(s.VariantId, quantity, null) },
        new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) },
        DeliveryCharge: deliveryCharge);

    [Fact]
    public async Task Normal_sale_without_a_delivery_charge_defaults_to_zero_and_is_unaffected()
    {
        var scene = await ArrangeAsync(price: 100m);

        var result = await CheckoutOkAsync(DeliverySale(scene, 1m, 100m, 0m));

        result.DeliveryCharge.Should().Be(0m);
        result.GrandTotal.Should().Be(100m);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync()).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Positive_delivery_charge_is_added_to_the_grand_total_exactly_once()
    {
        var scene = await ArrangeAsync(price: 100m);

        var result = await CheckoutOkAsync(DeliverySale(scene, 1m, 160m, deliveryCharge: 60m));

        result.DeliveryCharge.Should().Be(60m);
        result.GrandTotal.Should().Be(160m);
        result.AmountPaid.Should().Be(160m);
        result.ChangeDue.Should().Be(0m);

        await InScopeAsync(async db =>
        {
            var sale = await db.Sales.SingleAsync();
            sale.DeliveryCharge.Should().Be(60m);
            return true;
        });
    }

    [Fact]
    public async Task Free_delivery_keeps_the_charge_at_zero_and_leaves_the_total_unchanged()
    {
        var scene = await ArrangeAsync(price: 75m);

        var result = await CheckoutOkAsync(DeliverySale(scene, 1m, 75m, deliveryCharge: 0m));

        result.DeliveryCharge.Should().Be(0m);
        result.GrandTotal.Should().Be(75m);
    }

    [Fact]
    public async Task Negative_delivery_charge_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(DeliverySale(scene, 1m, 200m, deliveryCharge: -10m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delivery_charge_with_more_than_two_decimal_places_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(DeliverySale(scene, 1m, 200m, deliveryCharge: 10.005m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retrying_the_same_client_request_never_applies_the_delivery_charge_twice()
    {
        var scene = await ArrangeAsync(price: 100m);
        var request = DeliverySale(scene, 1m, 160m, deliveryCharge: 60m);

        var first = await CheckoutOkAsync(request);
        var second = await CheckoutOkAsync(request); // same ClientRequestId

        second.SaleId.Should().Be(first.SaleId);
        second.WasExistingRequest.Should().BeTrue();
        second.DeliveryCharge.Should().Be(60m);
        second.GrandTotal.Should().Be(160m);

        await InScopeAsync(async db =>
        {
            (await db.Sales.CountAsync()).Should().Be(1);
            return true;
        });
    }
}
