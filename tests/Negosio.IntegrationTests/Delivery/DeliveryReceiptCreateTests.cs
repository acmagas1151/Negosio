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

public class DeliveryReceiptCreateTests : IntegrationTest
{
    public DeliveryReceiptCreateTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, string SaleNumber, Guid SaleItemId, decimal DeliveryRequiredQuantity);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>One completed sale with <paramref name="deliveryRequiredQuantity"/> of its single line
    /// marked for delivery; the rest stays Take-now.</summary>
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

        return new Scene(sale.SaleId, sale.SaleNumber, sale.Items[0].SaleItemId, deliveryRequiredQuantity);
    }

    private static CreateDeliveryReceiptRequest Req(Scene s, decimal quantity, DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", "0917 111 2222", "Leave at guardhouse",
        new[] { new FulfillmentItemInput(s.SaleItemId, quantity) });

    [Fact]
    public async Task Create_persists_a_pending_schedule_with_only_its_own_assigned_items()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        dr.SequenceNumber.Should().Be(1);
        dr.Status.Should().Be(FulfillmentStatus.Pending);
        dr.ScheduledDate.Should().Be(Today);
        dr.RelatedSaleNumber.Should().Be(scene.SaleNumber);
        dr.Items.Should().ContainSingle();
        dr.Items[0].SaleItemId.Should().Be(scene.SaleItemId);
        dr.Items[0].Quantity.Should().Be(4m); // not the full sale quantity — a partial delivery
    }

    [Fact]
    public async Task Second_schedule_for_the_same_sale_gets_sequence_number_two()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));

        var second = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 2m));
        var dr = (await second.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        dr.SequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Create_rejects_a_quantity_exceeding_what_remains_available_to_schedule()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m)); // 2 left available

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 3m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_scheduling_a_quantity_that_was_never_marked_for_delivery()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m); // entirely Take-now

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 1m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_blank_recipient_name_or_address()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "  ", "123 Ayala Ave", null, null,
                new[] { new FulfillmentItemInput(scene.SaleItemId, 1m) })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_an_empty_item_list()
    {
        var scene = await ArrangeSaleAsync();
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan", "123 Ayala Ave", null, null,
                Array.Empty<FulfillmentItemInput>()));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_duplicate_sale_item_within_one_delivery()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan", "123 Ayala Ave", null, null, new[]
            {
                new FulfillmentItemInput(scene.SaleItemId, 2m),
                new FulfillmentItemInput(scene.SaleItemId, 2m),
            }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_a_scheduled_date_before_business_local_today()
    {
        var scene = await ArrangeSaleAsync();
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            Req(scene, 1m, Today.AddDays(-1)));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_is_rejected_for_a_voided_sale()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/void", new VoidSaleRequest("test"))).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 1m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Batch_creates_every_schedule_with_ascending_sequence_numbers()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var batchId = Guid.NewGuid();

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch",
            new CreateDeliveryReceiptBatchRequest(batchId, new[]
            {
                Req(scene, 4m),
                Req(scene, 2m, Today.AddDays(1)),
            }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = (await response.Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options))!;

        result.WasExistingBatch.Should().BeFalse();
        result.Created.Should().HaveCount(2);
        result.Created.Select(d => d.SequenceNumber).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public async Task Batch_rejects_two_schedules_that_together_over_allocate_the_same_sale_item()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch",
            new CreateDeliveryReceiptBatchRequest(Guid.NewGuid(), new[] { Req(scene, 4m), Req(scene, 3m) })); // 4+3 > 6

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(0); // nothing partially applied
            return true;
        });
    }

    // Final whole-branch review, Finding 3: the batch idempotency fast path used to run BEFORE
    // LoadDeliverableSaleAsync (and therefore before GuardBranchAsync). A branch-scoped caller who
    // knew both a foreign SaleId and the exact BatchRequestId originally used against it could hit
    // the fast path and receive that batch's full FulfillmentScheduleDto rows without ever being
    // branch-checked. The fix reorders CreateBatchAsync so the branch guard always runs first.
    [Fact]
    public async Task Batch_fast_path_never_bypasses_the_branch_guard_for_a_known_BatchRequestId()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var register = await CreateRegisterAsync(mainId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(mainId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            mainId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null, 6m) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1500m) }));

        var batchId = Guid.NewGuid();
        var request = new CreateDeliveryReceiptBatchRequest(batchId, new[]
        {
            new CreateDeliveryReceiptRequest(Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
                new[] { new FulfillmentItemInput(sale.Items[0].SaleItemId, 4m) }),
        });

        // Owner (unrestricted) creates the real batch at MAIN first.
        var original = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts/batch", request);
        original.StatusCode.Should().Be(HttpStatusCode.Created);

        // A cashier scoped to a DIFFERENT branch somehow learns this exact SaleId + BatchRequestId and
        // replays the identical request. Before the fix, this hit the pre-guard fast path and returned
        // MAIN's FulfillmentScheduleDto rows (recipient, address, delivery charge, etc.) as a 201 without
        // ever passing GuardBranchAsync. It must now be rejected as if the sale doesn't exist.
        var bgcCashierToken = await AddTenantUserTokenAsync("bgc.cashier@example.com", UserRole.Cashier, bgc.Id);
        Authorize(bgcCashierToken);

        var replay = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts/batch", request);
        replay.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retrying_the_same_BatchRequestId_returns_the_original_rows_without_duplicating()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var request = new CreateDeliveryReceiptBatchRequest(Guid.NewGuid(), new[] { Req(scene, 4m) });

        var first = await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch", request))
            .Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options);
        var second = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch", request);
        var secondDto = (await second.Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options))!;

        secondDto.WasExistingBatch.Should().BeTrue();
        secondDto.Created.Single().Id.Should().Be(first!.Created.Single().Id);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task List_for_sale_returns_every_schedule_ordered_by_sequence_number()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 2m));

        var list = await Client.GetFromJsonAsync<List<FulfillmentScheduleDto>>(
            $"/api/sales/{scene.SaleId}/delivery-receipts", TestJson.Options);

        list.Should().HaveCount(2);
        list![0].SequenceNumber.Should().Be(1);
        list[1].SequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Get_by_id_returns_a_stable_snapshot_after_a_catalog_rename()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m)))
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
    public async Task Fulfillment_summary_reflects_partial_scheduling_and_gates_CanCreateDelivery()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.AwaitingDelivery);
        summary.CanCreateDelivery.Should().BeTrue(); // 2 units still available
        summary.Items[0].DeliveryPendingQuantity.Should().Be(4m);
        summary.Items[0].DeliveryUnscheduledQuantity.Should().Be(2m);

        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 2m));
        var full = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        full!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.AwaitingDelivery);
        full.CanCreateDelivery.Should().BeFalse();
    }

    /// <summary>The most common real call: opening a sale's fulfillment summary before anything has been
    /// scheduled. The per-sale-item allocation map only has keys for items that already appear on some
    /// delivery, so this path must tolerate a missing key rather than throwing — the other two summary
    /// tests both fetch after a delivery exists, or with nothing marked for delivery at all (empty item
    /// list), so neither of them reaches the lookup with an unscheduled item.</summary>
    [Fact]
    public async Task Fulfillment_summary_for_a_sale_with_no_deliveries_yet_reports_NeedsScheduling()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m); // no deliveries created

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.NeedsScheduling);
        summary.CanCreateDelivery.Should().BeTrue();
        summary.Items.Should().ContainSingle();
        summary.Items[0].DeliveryPendingQuantity.Should().Be(0m);
        summary.Items[0].DeliveredQuantity.Should().Be(0m);
        summary.Items[0].DeliveryUnscheduledQuantity.Should().Be(6m);
    }

    [Fact]
    public async Task A_sale_with_nothing_marked_for_delivery_reports_NotApplicable()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.NotApplicable);
        summary.CanCreateDelivery.Should().BeFalse();
    }
}
