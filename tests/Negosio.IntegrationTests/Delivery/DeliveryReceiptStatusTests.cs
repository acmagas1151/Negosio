using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptStatusTests : IntegrationTest
{
    public DeliveryReceiptStatusTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, Guid BranchId, Guid SaleItemId);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, decimal deliveryRequiredQuantity = 6m, decimal price = 100m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null, deliveryRequiredQuantity) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + 500m) }));

        return new Scene(sale.SaleId, branchId, sale.Items[0].SaleItemId);
    }

    private async Task<DeliveryReceiptDto> CreateDeliveryAsync(Scene s, decimal quantity)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
                new[] { new CreateDeliveryReceiptItemInput(s.SaleItemId, quantity) }));
        return (await response.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Mark_delivered_transitions_pending_to_delivered_and_stamps_audit_fields()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);

        var response = await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var delivered = (await response.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        delivered.Status.Should().Be(DeliveryStatus.Delivered);
        delivered.DeliveredAtUtc.Should().NotBeNull();
        delivered.DeliveredByName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Mark_delivered_twice_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_requires_a_non_blank_reason()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("   "));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_transitions_pending_to_cancelled_and_releases_quantity_to_unscheduled()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var dr = await CreateDeliveryAsync(scene, 4m); // 2 left available

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("Customer rescheduled"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = (await response.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        cancelled.Status.Should().Be(DeliveryStatus.Cancelled);
        cancelled.CancellationReason.Should().Be("Customer rescheduled");
        cancelled.Items.Should().ContainSingle(); // its own item rows are preserved, unchanged, as history

        var summary = await Client.GetFromJsonAsync<SaleDeliverySummaryDto>($"/api/sales/{scene.SaleId}/delivery-summary", TestJson.Options);
        summary!.Items[0].AvailableToScheduleQuantity.Should().Be(6m); // fully released
        summary.CanCreateDelivery.Should().BeTrue();
    }

    [Fact]
    public async Task Rescheduling_after_a_cancel_creates_a_new_record_and_never_reactivates_the_cancelled_one()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var first = await CreateDeliveryAsync(scene, 4m);
        await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel", new CancelDeliveryReceiptRequest("Wrong address"));

        var second = await CreateDeliveryAsync(scene, 4m); // re-schedule the same released quantity

        second.Id.Should().NotBe(first.Id);
        second.SequenceNumber.Should().Be(2);
        second.Status.Should().Be(DeliveryStatus.Pending);

        var reloadedFirst = await Client.GetFromJsonAsync<DeliveryReceiptDto>($"/api/delivery-receipts/{first.Id}", TestJson.Options);
        reloadedFirst!.Status.Should().Be(DeliveryStatus.Cancelled); // untouched
    }

    [Fact]
    public async Task Cancel_after_delivered_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("Too late"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cashier_may_mark_delivered_but_not_cancel()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        Authorize(await AddTenantUserTokenAsync("cashier@example.com", UserRole.Cashier, scene.BranchId));

        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cashier_cannot_cancel_a_delivery()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        Authorize(await AddTenantUserTokenAsync("cashier2@example.com", UserRole.Cashier, scene.BranchId));

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("Changed mind"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
