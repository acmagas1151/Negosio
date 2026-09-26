using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

/// <summary>
/// Task 3 (fulfillment-recovery-fixes) — end-to-end coverage that voiding a sale through the real
/// <c>/api/sales/{id}/void</c> endpoint now cascades into <c>VoidSaleService</c>'s call to
/// <see cref="IDeliveryReceiptService.CancelActiveScheduleForVoidedSaleAsync"/>, rather than stranding an
/// active Pending schedule. Task 2's <c>DeliveryReceiptConcurrencyTests</c> already covers the cascade
/// method itself directly (it had no caller yet); this file exercises it through the real void pipeline.
/// </summary>
public class VoidSaleFulfillmentTests : IntegrationTest
{
    public VoidSaleFulfillmentTests(NegosioApiFactory factory) : base(factory) { }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    private static CreateDeliveryReceiptRequest DeliveryReq() => new(
        Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", "0917 111 2222", null);

    private static CreatePickupRequest PickupReq() => new(
        Today, "Juan Dela Cruz", "0917 111 2222", null);

    /// <summary>One completed sale whose single line is entirely earmarked for <paramref name="method"/>
    /// (or entirely take-now, when <paramref name="method"/> is <see cref="FulfillmentMethod.TakeNow"/>).</summary>
    private async Task<Guid> CheckoutSaleAsync(FulfillmentMethod method, decimal qty = 10m)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (100m * qty) + 500m) },
            Method: method));

        return sale.SaleId;
    }

    [Fact]
    public async Task VoidAsync_CancelsAnActivePendingDeliverySchedule()
    {
        var saleId = await CheckoutSaleAsync(FulfillmentMethod.Delivery);
        var created = await Client.PostAsJsonAsync($"/api/sales/{saleId}/delivery-receipts", DeliveryReq());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var voidResponse = await Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new VoidSaleRequest("Customer changed their mind"));
        voidResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await voidResponse.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.Sale.Status.Should().Be(SaleStatus.Voided);

        var fulfillment = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{saleId}/fulfillment", TestJson.Options);
        fulfillment!.ActiveSchedule.Should().BeNull();
        fulfillment.Deliveries.Should().ContainSingle();
        fulfillment.Deliveries[0].Status.Should().Be(FulfillmentStatus.Cancelled);
        fulfillment.Deliveries[0].CancellationDisposition.Should().Be(CancellationDisposition.SaleVoided);

        // The schedule can no longer be completed — CompleteAsync's existing Pending-only gate now applies.
        var deliverResponse = await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);
        deliverResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await deliverResponse.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code
            .Should().Be(Negosio.Application.Common.ErrorCodes.DeliveryReceiptNotPending);
    }

    [Fact]
    public async Task VoidAsync_CancelsAnActivePendingPickupSchedule()
    {
        var saleId = await CheckoutSaleAsync(FulfillmentMethod.Pickup);
        var created = await Client.PostAsJsonAsync($"/api/sales/{saleId}/pickups", PickupReq());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var pickup = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var voidResponse = await Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new VoidSaleRequest("Customer changed their mind"));
        voidResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var fulfillment = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{saleId}/fulfillment", TestJson.Options);
        fulfillment!.ActiveSchedule.Should().BeNull();
        fulfillment.Pickups.Should().ContainSingle();
        fulfillment.Pickups[0].Status.Should().Be(FulfillmentStatus.Cancelled);
        fulfillment.Pickups[0].CancellationDisposition.Should().Be(CancellationDisposition.SaleVoided);

        var claimResponse = await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null);
        claimResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await claimResponse.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code
            .Should().Be(Negosio.Application.Common.ErrorCodes.DeliveryReceiptNotPending);
    }

    [Fact]
    public async Task VoidAsync_StillVoidsATakeNowSaleWithNoSchedule()
    {
        // Regression guard: the cascade must be a true no-op for the common case (no active schedule).
        var saleId = await CheckoutSaleAsync(FulfillmentMethod.TakeNow);

        var voidResponse = await Client.PostAsJsonAsync($"/api/sales/{saleId}/void", new VoidSaleRequest("Test"));

        voidResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await voidResponse.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.Sale.Status.Should().Be(SaleStatus.Voided);

        var fulfillment = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{saleId}/fulfillment", TestJson.Options);
        fulfillment!.ActiveSchedule.Should().BeNull();
        fulfillment.Deliveries.Should().BeEmpty();
        fulfillment.Pickups.Should().BeEmpty();
    }
}
