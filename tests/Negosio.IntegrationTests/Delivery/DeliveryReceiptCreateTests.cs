using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

/// <summary>
/// Creating a schedule is now whole-sale: a sale has at most one active (non-Cancelled) schedule, of
/// either method, at a time, and that schedule always covers every item currently earmarked for its
/// method at that item's full required quantity. There is no per-line selection, no partial allocation,
/// and no batch endpoint any more — see the plan's Task 2 for the simplification this file exercises.
/// </summary>
public class DeliveryReceiptCreateTests : IntegrationTest
{
    public DeliveryReceiptCreateTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, string SaleNumber, Guid SaleItemId, decimal Quantity);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>One completed sale whose single line is entirely earmarked for <paramref name="method"/>
    /// (or entirely take-now, when <paramref name="method"/> is <see cref="FulfillmentMethod.TakeNow"/>) —
    /// whole-sale intent, per Task 1.</summary>
    private async Task<Scene> ArrangeSaleAsync(
        decimal qty = 10m, FulfillmentMethod method = FulfillmentMethod.Delivery, decimal price = 100m)
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
            Method: method));

        return new Scene(sale.SaleId, sale.SaleNumber, sale.Items[0].SaleItemId, qty);
    }

    private static CreateDeliveryReceiptRequest Req(DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", "0917 111 2222", "Leave at guardhouse");

    private static CreatePickupRequest PickupReq(DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "0917 111 2222", null);

    [Fact]
    public async Task Create_persists_a_pending_schedule_covering_the_full_delivery_required_quantity()
    {
        var scene = await ArrangeSaleAsync(qty: 10m);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        dr.SequenceNumber.Should().Be(1);
        dr.Status.Should().Be(FulfillmentStatus.Pending);
        dr.ScheduledDate.Should().Be(Today);
        dr.RelatedSaleNumber.Should().Be(scene.SaleNumber);
        dr.Items.Should().ContainSingle();
        dr.Items[0].SaleItemId.Should().Be(scene.SaleItemId);
        dr.Items[0].Quantity.Should().Be(scene.Quantity); // the whole line, never a subset
    }

    [Fact]
    public async Task Rescheduling_via_a_DeliverLater_cancel_gets_sequence_number_two()
    {
        var scene = await ArrangeSaleAsync();
        var first = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req()))
            .Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        // Per spec (Task 3B), DeliverLater's cancel creates the reschedule itself, atomically — there is
        // no longer a separate follow-up create call (the sale can have only one active schedule at a
        // time, and the replacement the cancel just created already occupies that slot).
        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel",
            new CancelDeliveryRequest("Wrong address", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null)));
        var dr = (await response.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))!.Replacement!;

        dr.SequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Create_rejects_scheduling_when_nothing_is_marked_for_delivery()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, method: FulfillmentMethod.TakeNow); // entirely Take-now

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_blank_recipient_name_or_address()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "  ", "123 Ayala Ave", null, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_a_scheduled_date_before_business_local_today()
    {
        var scene = await ArrangeSaleAsync();
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            Req(Today.AddDays(-1)));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_is_rejected_for_a_voided_sale()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/void", new VoidSaleRequest("test"))).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_for_sale_returns_every_schedule_ordered_by_sequence_number()
    {
        var scene = await ArrangeSaleAsync();
        var first = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req()))
            .Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
        // Per spec (Task 3B), DeliverLater's cancel creates the reschedule (sequence 2) itself.
        await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel",
            new CancelDeliveryRequest("Reschedule", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null)));

        var list = await Client.GetFromJsonAsync<List<FulfillmentScheduleDto>>(
            $"/api/sales/{scene.SaleId}/delivery-receipts", TestJson.Options);

        list.Should().HaveCount(2);
        list![0].SequenceNumber.Should().Be(1);
        list[1].SequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Get_by_id_returns_a_stable_snapshot_after_a_catalog_rename()
    {
        var scene = await ArrangeSaleAsync();
        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req()))
            .Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var productName = dr.Items[0].ProductName;
        await InScopeAsync(async db =>
        {
            var saleItem = await db.SaleItems.SingleAsync(i => i.Id == scene.SaleItemId);
            var productId = await db.ProductVariants
                .Where(v => v.Id == saleItem.ProductVariantId)
                .Select(v => v.ProductId)
                .SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Products SET Name = {"Totally Different Name"} WHERE Id = {productId}");
            return true;
        });

        var again = await Client.GetFromJsonAsync<FulfillmentScheduleDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        again!.Items[0].ProductName.Should().Be(productName);
    }

    [Fact]
    public async Task Fulfillment_summary_reflects_a_created_schedule_as_the_active_schedule()
    {
        var scene = await ArrangeSaleAsync(qty: 10m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req());

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.PendingDelivery);
        summary.ActiveSchedule.Should().NotBeNull();
        summary.ActiveSchedule!.Method.Should().Be(FulfillmentMethod.Delivery);
        summary.ActiveSchedule.Status.Should().Be(FulfillmentStatus.Pending);
        summary.Deliveries.Should().ContainSingle();
        summary.Pickups.Should().BeEmpty();
    }

    /// <summary>The most common real call: opening a sale's fulfillment summary before anything has been
    /// scheduled. In the simplified model this collapses to TakeNow — the same value as a sale with no
    /// delivery intent at all — since status is now read directly off the schedule, and there is none
    /// yet, regardless of the item's delivery intent.</summary>
    [Fact]
    public async Task Fulfillment_summary_for_a_sale_with_no_schedule_yet_reports_TakeNow()
    {
        var scene = await ArrangeSaleAsync(qty: 10m); // no deliveries created

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.TakeNow);
        summary.ActiveSchedule.Should().BeNull();
        summary.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sale_with_nothing_marked_for_delivery_reports_TakeNow()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, method: FulfillmentMethod.TakeNow);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.TakeNow);
        summary.ActiveSchedule.Should().BeNull();
    }

    // ---- The simplified one-active-schedule invariant (spec's backend acceptance criteria) ----

    [Fact]
    public async Task Creating_a_pickup_when_an_active_delivery_already_exists_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req()))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId && d.Method == FulfillmentMethod.Pickup))
                .Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Creating_a_second_delivery_when_one_is_already_pending_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req()))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task A_new_delivery_covers_every_item_on_the_sale_at_full_quantity()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId1) = await SeedStockedProductAsync(branchId, category.Id, sku: "SKU-A", sellingPrice: 100m, openingStock: 20m);
        var (_, variantId2) = await SeedStockedProductAsync(branchId, category.Id, sku: "SKU-B", sellingPrice: 50m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[]
            {
                new CheckoutItemInput(variantId1, 4m, null),
                new CheckoutItemInput(variantId2, 6m, null),
            },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1500m) },
            Method: FulfillmentMethod.Delivery));

        var response = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts", Req());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        dr.Items.Should().HaveCount(2); // every sale item, not a subset
        dr.Items.Should().Contain(i => i.SaleItemId == sale.Items[0].SaleItemId && i.Quantity == 4m);
        dr.Items.Should().Contain(i => i.SaleItemId == sale.Items[1].SaleItemId && i.Quantity == 6m);
    }
}
