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

    private sealed record Scene(Guid SaleId, Guid BranchId, Guid SaleItemId, decimal Quantity);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>One completed sale whose single line is entirely earmarked for delivery — whole-sale
    /// intent, per Task 1.</summary>
    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, decimal price = 100m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + 500m) },
            Method: FulfillmentMethod.Delivery));

        return new Scene(sale.SaleId, branchId, sale.Items[0].SaleItemId, qty);
    }

    private async Task<FulfillmentScheduleDto> CreateDeliveryAsync(Scene s)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null));
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Mark_delivered_transitions_pending_to_delivered_and_stamps_audit_fields()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene);

        var response = await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var delivered = (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        delivered.Status.Should().Be(FulfillmentStatus.Completed);
        delivered.CompletedAtUtc.Should().NotBeNull();
        delivered.CompletedByName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Mark_delivered_twice_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene);
        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_requires_a_non_blank_reason()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene);

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryRequest("   ", CancellationDisposition.DeliverLater, null));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_with_DeliverLater_transitions_pending_to_cancelled_and_creates_a_pending_replacement()
    {
        var scene = await ArrangeSaleAsync(qty: 10m);
        var dr = await CreateDeliveryAsync(scene); // covers the whole line

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel",
            new CancelDeliveryRequest("Customer rescheduled", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "456 Ortigas Ave", null, null)));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))!;
        var cancelled = result.Cancelled;
        cancelled.Status.Should().Be(FulfillmentStatus.Cancelled);
        cancelled.CancellationReason.Should().Be("Customer rescheduled");
        cancelled.CancellationDisposition.Should().Be(CancellationDisposition.DeliverLater);
        cancelled.Items.Should().ContainSingle(); // its own item rows are preserved, unchanged, as history

        // Per spec, DeliverLater must end with exactly one active replacement schedule covering the
        // complete sale — never a bare release back to "unscheduled" (Task 3B).
        result.Replacement.Should().NotBeNull();
        var replacement = result.Replacement!;
        replacement.Status.Should().Be(FulfillmentStatus.Pending);
        replacement.Method.Should().Be(FulfillmentMethod.Delivery);
        replacement.DeliveryAddress.Should().Be("456 Ortigas Ave");
        replacement.Items.Should().ContainSingle();
        replacement.Items[0].Quantity.Should().Be(scene.Quantity); // full sale quantity, whole-sale allocation

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>($"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        summary!.ActiveSchedule.Should().NotBeNull();
        summary.ActiveSchedule!.Id.Should().Be(replacement.Id);
        summary.ActiveSchedule!.Status.Should().Be(FulfillmentStatus.Pending);
        summary.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.PendingDelivery);
    }

    [Fact]
    public async Task Rescheduling_via_DeliverLater_creates_a_new_record_and_never_reactivates_the_cancelled_one()
    {
        var scene = await ArrangeSaleAsync(qty: 10m);
        var first = await CreateDeliveryAsync(scene);
        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel",
            new CancelDeliveryRequest("Wrong address", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "456 Ortigas Ave", null, null)));
        var result = (await response.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))!;
        var second = result.Replacement!; // the cancel itself creates the reschedule now (Task 3B)

        second.Id.Should().NotBe(first.Id);
        second.SequenceNumber.Should().Be(2);
        second.Status.Should().Be(FulfillmentStatus.Pending);

        var reloadedFirst = await Client.GetFromJsonAsync<FulfillmentScheduleDto>($"/api/delivery-receipts/{first.Id}", TestJson.Options);
        reloadedFirst!.Status.Should().Be(FulfillmentStatus.Cancelled); // untouched
    }

    [Fact]
    public async Task Cancel_after_delivered_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene);
        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel",
            new CancelDeliveryRequest("Too late", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "456 Ortigas Ave", null, null)));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cashier_may_mark_delivered_but_not_cancel()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene);
        Authorize(await AddTenantUserTokenAsync("cashier@example.com", UserRole.Cashier, scene.BranchId));

        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cashier_cannot_cancel_a_delivery()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene);
        Authorize(await AddTenantUserTokenAsync("cashier2@example.com", UserRole.Cashier, scene.BranchId));

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel",
            new CancelDeliveryRequest("Changed mind", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "456 Ortigas Ave", null, null)));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
