using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

/// <summary>
/// Pickup is the third fulfillment method: quantity the customer collects at the branch after checkout.
/// It shares the DeliveryReceipts table with Delivery (discriminated by <c>Method</c>) but keeps an
/// entirely separate quantity pool, its own per-sale sequence numbering, and its own completion verb
/// (claim, never deliver).
/// </summary>
public class PickupTests : IntegrationTest
{
    public PickupTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, Guid BranchId, Guid SaleItemId);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>One completed sale whose single line carries <paramref name="deliveryRequiredQuantity"/>
    /// of delivery intent and <paramref name="pickupRequiredQuantity"/> of pickup intent; the remainder is
    /// take-now.</summary>
    private async Task<Scene> ArrangeSaleAsync(
        decimal qty = 10m, decimal deliveryRequiredQuantity = 0m, decimal pickupRequiredQuantity = 6m, decimal price = 100m)
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

        var saleItemId = sale.Items[0].SaleItemId;
        await SetFulfillmentIntentAsync(saleItemId, deliveryRequiredQuantity, pickupRequiredQuantity);

        return new Scene(sale.SaleId, branchId, saleItemId);
    }

    private static CreatePickupRequest PickupReq(Scene s, decimal quantity, DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "0917 111 2222", "Collect at the counter",
        new[] { new FulfillmentItemInput(s.SaleItemId, quantity) });

    private static CreateDeliveryReceiptRequest DeliveryReq(Scene s, decimal quantity, DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
        new[] { new FulfillmentItemInput(s.SaleItemId, quantity) });

    private async Task<FulfillmentScheduleDto> CreatePickupAsync(Scene s, decimal quantity, DateOnly? date = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/pickups", PickupReq(s, quantity, date));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    private async Task<FulfillmentScheduleDto> CreateDeliveryAsync(Scene s, decimal quantity)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/delivery-receipts", DeliveryReq(s, quantity));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    private Task<SaleFulfillmentSummaryDto?> GetSummaryAsync(Scene s) =>
        Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>($"/api/sales/{s.SaleId}/fulfillment", TestJson.Options);

    /// <summary>Sequence numbers are per-sale AND per-method, so a sale that already has "Delivery 1"
    /// still names its first pickup "Pickup 1" — not "2". A pickup also never carries an address.</summary>
    [Fact]
    public async Task Create_pickup_numbers_independently_of_deliveries_and_carries_no_address()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 3m, pickupRequiredQuantity: 5m);

        var delivery = await CreateDeliveryAsync(scene, 3m);
        delivery.SequenceNumber.Should().Be(1);
        delivery.Method.Should().Be(FulfillmentMethod.Delivery);

        var pickup = await CreatePickupAsync(scene, 4m);

        pickup.SequenceNumber.Should().Be(1); // NOT 2 — the two methods number separately
        pickup.Method.Should().Be(FulfillmentMethod.Pickup);
        pickup.Status.Should().Be(FulfillmentStatus.Pending);
        pickup.DeliveryAddress.Should().BeNull();
        pickup.DeliveryCharge.Should().Be(0m);
        pickup.Items.Should().ContainSingle();
        pickup.Items[0].Quantity.Should().Be(4m);
    }

    [Fact]
    public async Task Create_pickup_rejects_a_quantity_exceeding_what_remains_pickup_available()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, pickupRequiredQuantity: 6m);
        await CreatePickupAsync(scene, 4m); // 2 left

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq(scene, 3m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Delivery intent and pickup intent are two separate pools. Quantity marked for DELIVERY
    /// can never be scheduled as a pickup without an explicit conversion.</summary>
    [Fact]
    public async Task Create_pickup_rejects_a_quantity_that_was_marked_for_delivery_not_pickup()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m, pickupRequiredQuantity: 0m);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq(scene, 1m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Mark_claimed_transitions_pending_to_completed_and_stamps_audit_fields()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene, 4m);

        var response = await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var claimed = (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        claimed.Status.Should().Be(FulfillmentStatus.Completed);
        claimed.CompletedAtUtc.Should().NotBeNull();
        claimed.CompletedByName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Mark_claimed_twice_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Marking_a_delivery_claimed_is_rejected()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 4m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var response = await Client.PostAsync($"/api/delivery-receipts/{delivery.Id}/claim", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Marking_a_pickup_delivered_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene, 4m);

        var response = await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Claimed is terminal — the customer already has the goods, so there is nothing left to
    /// cancel, reschedule or convert.</summary>
    [Fact]
    public async Task A_claimed_pickup_cannot_be_cancelled()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/pickups/{pickup.Id}/cancel",
            new CancelPickupRequest("Too late", CancellationDisposition.PickupLater, null));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Claimed_quantity_is_no_longer_available_to_schedule()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, pickupRequiredQuantity: 6m);
        var pickup = await CreatePickupAsync(scene, 6m);

        var beforeClaim = await GetSummaryAsync(scene);
        beforeClaim!.Items[0].PickupPendingQuantity.Should().Be(6m);
        beforeClaim.Items[0].PickupUnscheduledQuantity.Should().Be(0m);
        beforeClaim.CanCreatePickup.Should().BeFalse();

        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null)).EnsureSuccessStatusCode();

        var afterClaim = await GetSummaryAsync(scene);
        afterClaim!.Items[0].ClaimedQuantity.Should().Be(6m);
        afterClaim.Items[0].PickupPendingQuantity.Should().Be(0m);
        afterClaim.Items[0].PickupUnscheduledQuantity.Should().Be(0m); // claimed is consumed, not released
        afterClaim.CanCreatePickup.Should().BeFalse();

        // And the API agrees: re-scheduling the claimed units is rejected.
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq(scene, 1m)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
