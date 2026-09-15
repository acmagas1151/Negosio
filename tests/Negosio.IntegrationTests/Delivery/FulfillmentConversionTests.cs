using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Common;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

/// <summary>
/// Cancelling a pending schedule always carries a DISPOSITION saying where its quantity went. Three of
/// the five dispositions move intent between the Delivery and Pickup pools and create a replacement
/// schedule; all five write an immutable FulfillmentConversion audit row. Cancellation, conversion,
/// audit row and replacement are one transaction — all commit or none do.
/// </summary>
public class FulfillmentConversionTests : IntegrationTest
{
    public FulfillmentConversionTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, Guid BranchId, Guid SaleItemId);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    private async Task<Scene> ArrangeSaleAsync(
        decimal qty = 10m,
        decimal deliveryRequiredQuantity = 6m,
        decimal pickupRequiredQuantity = 0m,
        decimal price = 100m,
        decimal deliveryCharge = 0m)
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
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + deliveryCharge + 500m) },
            DeliveryCharge: deliveryCharge));

        var saleItemId = sale.Items[0].SaleItemId;
        await SetFulfillmentIntentAsync(saleItemId, deliveryRequiredQuantity, pickupRequiredQuantity);

        return new Scene(sale.SaleId, branchId, saleItemId);
    }

    private async Task<FulfillmentScheduleDto> CreateDeliveryAsync(Scene s, decimal quantity)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
                new[] { new FulfillmentItemInput(s.SaleItemId, quantity) }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    private async Task<FulfillmentScheduleDto> CreatePickupAsync(Scene s, decimal quantity)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/pickups",
            new CreatePickupRequest(Today, "Juan Dela Cruz", null, null,
                new[] { new FulfillmentItemInput(s.SaleItemId, quantity) }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    private async Task<(HttpStatusCode Status, CancellationResultDto? Body)> CancelDeliveryAsync(
        Guid id, CancellationDisposition disposition, PickupReplacementInput? replacement, string reason = "Customer request")
    {
        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{id}/cancel",
            new CancelDeliveryRequest(reason, disposition, replacement));
        return response.IsSuccessStatusCode
            ? (response.StatusCode, await response.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))
            : (response.StatusCode, null);
    }

    private async Task<(HttpStatusCode Status, CancellationResultDto? Body)> CancelPickupAsync(
        Guid id, CancellationDisposition disposition, DeliveryReplacementInput? replacement, string reason = "Customer request")
    {
        var response = await Client.PostAsJsonAsync($"/api/pickups/{id}/cancel",
            new CancelPickupRequest(reason, disposition, replacement));
        return response.IsSuccessStatusCode
            ? (response.StatusCode, await response.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))
            : (response.StatusCode, null);
    }

    private Task<SaleFulfillmentSummaryDto?> GetSummaryAsync(Scene s) =>
        Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>($"/api/sales/{s.SaleId}/fulfillment", TestJson.Options);

    private Task<(decimal Delivery, decimal Pickup, decimal TakeNow)> GetIntentAsync(Guid saleItemId) =>
        InScopeAsync(async db =>
        {
            var i = await db.SaleItems.AsNoTracking().SingleAsync(x => x.Id == saleItemId);
            return (i.DeliveryRequiredQuantity, i.PickupRequiredQuantity, i.TakeNowQuantity);
        });

    private Task<List<FulfillmentConversion>> GetConversionsAsync(Guid saleId) =>
        InScopeAsync(db => db.FulfillmentConversions.AsNoTracking()
            .Where(c => c.SaleId == saleId)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync());

    // ---- Delivery dispositions ----

    /// <summary>"Deliver later" is a plain release: the quantity returns to delivery-unscheduled, the
    /// pickup pool is untouched, and no replacement is created — but the audit trail still records WHY
    /// the quantity came back, as a same-method conversion event.</summary>
    [Fact]
    public async Task Delivery_cancelled_with_DeliverLater_releases_to_delivery_unscheduled_and_records_a_same_method_event()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m, pickupRequiredQuantity: 2m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var (status, body) = await CancelDeliveryAsync(delivery.Id, CancellationDisposition.DeliverLater, null, "Van broke down");
        status.Should().Be(HttpStatusCode.OK);

        body!.Cancelled.Status.Should().Be(FulfillmentStatus.Cancelled);
        body.Cancelled.CancellationDisposition.Should().Be(CancellationDisposition.DeliverLater);
        body.Replacement.Should().BeNull();

        var intent = await GetIntentAsync(scene.SaleItemId);
        intent.Delivery.Should().Be(6m); // unchanged — a release moves nothing between pools
        intent.Pickup.Should().Be(2m);

        var summary = await GetSummaryAsync(scene);
        summary!.Items[0].DeliveryUnscheduledQuantity.Should().Be(6m);
        summary.Items[0].DeliveryPendingQuantity.Should().Be(0m);
        summary.Items[0].PickupUnscheduledQuantity.Should().Be(2m);

        var conversions = await GetConversionsAsync(scene.SaleId);
        var conversion = conversions.Should().ContainSingle().Subject;
        conversion.FromMethod.Should().Be(FulfillmentMethod.Delivery);
        conversion.ToMethod.Should().Be(FulfillmentMethod.Delivery);
        conversion.Quantity.Should().Be(4m);
        conversion.SourceRecordId.Should().Be(delivery.Id);
        conversion.ReplacementRecordId.Should().BeNull();
        conversion.Reason.Should().Be("Van broke down");
    }

    [Fact]
    public async Task Delivery_cancelled_with_ConvertToPickup_creates_a_pending_pickup_and_moves_intent()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var (status, body) = await CancelDeliveryAsync(
            delivery.Id, CancellationDisposition.ConvertToPickup,
            new PickupReplacementInput(Today.AddDays(1), "Maria Santos", "0917 000 1111", "Collect at counter"),
            "Customer will collect");
        status.Should().Be(HttpStatusCode.OK);

        body!.Cancelled.Status.Should().Be(FulfillmentStatus.Cancelled);
        body.Cancelled.CancellationDisposition.Should().Be(CancellationDisposition.ConvertToPickup);

        body.Replacement.Should().NotBeNull();
        var replacement = body.Replacement!;
        replacement.Method.Should().Be(FulfillmentMethod.Pickup);
        replacement.Status.Should().Be(FulfillmentStatus.Pending);
        replacement.SequenceNumber.Should().Be(1); // first pickup on this sale
        replacement.ScheduledDate.Should().Be(Today.AddDays(1));
        replacement.RecipientName.Should().Be("Maria Santos");
        replacement.DeliveryAddress.Should().BeNull();
        replacement.Items.Should().ContainSingle();
        replacement.Items[0].SaleItemId.Should().Be(scene.SaleItemId);
        replacement.Items[0].Quantity.Should().Be(4m); // same quantities carried across

        var intent = await GetIntentAsync(scene.SaleItemId);
        intent.Delivery.Should().Be(2m); // 6 - 4
        intent.Pickup.Should().Be(4m);   // 0 + 4
        intent.TakeNow.Should().Be(4m);  // untouched

        var conversion = (await GetConversionsAsync(scene.SaleId)).Should().ContainSingle().Subject;
        conversion.FromMethod.Should().Be(FulfillmentMethod.Delivery);
        conversion.ToMethod.Should().Be(FulfillmentMethod.Pickup);
        conversion.Quantity.Should().Be(4m);
        conversion.SourceRecordId.Should().Be(delivery.Id);
        conversion.ReplacementRecordId.Should().Be(replacement.Id);
    }

    /// <summary>
    /// The single most important guarantee in this feature. "The customer picked it up instead" must
    /// NEVER leave the delivery looking Delivered, and must NEVER create a take-now adjustment: it
    /// cancels the delivery with that disposition and records a SEPARATE pickup that is already Claimed.
    /// </summary>
    [Fact]
    public async Task Delivery_cancelled_with_CustomerPickedUpInstead_never_completes_the_delivery_and_claims_the_replacement()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);
        var takeNowBefore = (await GetIntentAsync(scene.SaleItemId)).TakeNow;

        var (status, body) = await CancelDeliveryAsync(
            delivery.Id, CancellationDisposition.CustomerPickedUpInstead,
            new PickupReplacementInput(Today, "Juan Dela Cruz", null, null),
            "Walked in and took it");
        status.Should().Be(HttpStatusCode.OK);

        body!.Cancelled.Status.Should().Be(FulfillmentStatus.Cancelled);
        body.Cancelled.Status.Should().NotBe(FulfillmentStatus.Completed);
        body.Cancelled.CompletedAtUtc.Should().BeNull();
        body.Cancelled.CancellationDisposition.Should().Be(CancellationDisposition.CustomerPickedUpInstead);

        var replacement = body.Replacement!;
        replacement.Method.Should().Be(FulfillmentMethod.Pickup);
        replacement.Status.Should().Be(FulfillmentStatus.Completed);
        replacement.CompletedAtUtc.Should().NotBeNull();
        replacement.CompletedByName.Should().NotBeNullOrEmpty();

        // Spec test 18: no take-now adjustment is ever created for this path.
        var intent = await GetIntentAsync(scene.SaleItemId);
        intent.TakeNow.Should().Be(takeNowBefore);
        intent.Delivery.Should().Be(2m);
        intent.Pickup.Should().Be(4m);

        var conversion = (await GetConversionsAsync(scene.SaleId)).Should().ContainSingle().Subject;
        conversion.FromMethod.Should().Be(FulfillmentMethod.Delivery);
        conversion.ToMethod.Should().Be(FulfillmentMethod.Pickup);
        conversion.ReplacementRecordId.Should().Be(replacement.Id);

        // And the claimed units are accounted for as claimed, not as take-now or as available.
        var summary = await GetSummaryAsync(scene);
        summary!.Items[0].ClaimedQuantity.Should().Be(4m);
        summary.Items[0].TakeNowQuantity.Should().Be(takeNowBefore);
        summary.Items[0].PickupUnscheduledQuantity.Should().Be(0m);
        summary.Items[0].DeliveryUnscheduledQuantity.Should().Be(2m);
    }

    // ---- Pickup dispositions ----

    [Fact]
    public async Task Pickup_cancelled_with_PickupLater_releases_back_to_pickup_unscheduled()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 6m);
        var pickup = await CreatePickupAsync(scene, 4m);

        var (status, body) = await CancelPickupAsync(pickup.Id, CancellationDisposition.PickupLater, null, "No show");
        status.Should().Be(HttpStatusCode.OK);
        body!.Cancelled.Status.Should().Be(FulfillmentStatus.Cancelled);
        body.Cancelled.CancellationDisposition.Should().Be(CancellationDisposition.PickupLater);
        body.Replacement.Should().BeNull();

        var intent = await GetIntentAsync(scene.SaleItemId);
        intent.Pickup.Should().Be(6m);
        intent.Delivery.Should().Be(0m);

        var summary = await GetSummaryAsync(scene);
        summary!.Items[0].PickupUnscheduledQuantity.Should().Be(6m);
        summary.Items[0].PickupPendingQuantity.Should().Be(0m);
        summary.CanCreatePickup.Should().BeTrue();

        var conversion = (await GetConversionsAsync(scene.SaleId)).Should().ContainSingle().Subject;
        conversion.FromMethod.Should().Be(FulfillmentMethod.Pickup);
        conversion.ToMethod.Should().Be(FulfillmentMethod.Pickup);
        conversion.SourceRecordId.Should().Be(pickup.Id);
        conversion.ReplacementRecordId.Should().BeNull();
    }

    [Fact]
    public async Task Pickup_cancelled_with_ConvertToDelivery_creates_a_pending_delivery_and_moves_intent_the_other_way()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 6m);
        var pickup = await CreatePickupAsync(scene, 4m);

        var (status, body) = await CancelPickupAsync(
            pickup.Id, CancellationDisposition.ConvertToDelivery,
            new DeliveryReplacementInput(Today.AddDays(2), "Maria Santos", "88 Katipunan Ave, QC", "0917 222 3333", "Ring twice"),
            "Customer asked us to deliver");
        status.Should().Be(HttpStatusCode.OK);

        var replacement = body!.Replacement!;
        replacement.Method.Should().Be(FulfillmentMethod.Delivery);
        replacement.Status.Should().Be(FulfillmentStatus.Pending);
        replacement.SequenceNumber.Should().Be(1); // first delivery on this sale
        replacement.DeliveryAddress.Should().Be("88 Katipunan Ave, QC");
        replacement.Items[0].Quantity.Should().Be(4m);

        var intent = await GetIntentAsync(scene.SaleItemId);
        intent.Pickup.Should().Be(2m);
        intent.Delivery.Should().Be(4m);

        var conversion = (await GetConversionsAsync(scene.SaleId)).Should().ContainSingle().Subject;
        conversion.FromMethod.Should().Be(FulfillmentMethod.Pickup);
        conversion.ToMethod.Should().Be(FulfillmentMethod.Delivery);
        conversion.ReplacementRecordId.Should().Be(replacement.Id);
    }

    // ---- Rejections ----

    [Fact]
    public async Task Cancelling_a_delivery_with_a_pickup_only_disposition_is_rejected()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var (status, _) = await CancelDeliveryAsync(delivery.Id, CancellationDisposition.PickupLater, null);
        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancelling_a_delivery_with_ConvertToPickup_but_no_replacement_details_is_rejected()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var (status, _) = await CancelDeliveryAsync(delivery.Id, CancellationDisposition.ConvertToPickup, null);
        status.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Spec test 4. "Take-now" describes goods handed over during checkout; it can never become true
    /// after the fact, so it is not a member of <see cref="CancellationDisposition"/> at all. Posting it
    /// as a raw string lands as a deserialization/validation failure on both cancel endpoints — this test
    /// exists to document that it can never quietly become an accepted value.
    /// </summary>
    [Fact]
    public async Task TakeNow_is_never_an_accepted_disposition_on_either_cancel_endpoint()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 4m);
        var delivery = await CreateDeliveryAsync(scene, 4m);
        var pickup = await CreatePickupAsync(scene, 4m);

        var deliveryResponse = await Client.PostAsJsonAsync($"/api/delivery-receipts/{delivery.Id}/cancel",
            new { reason = "Customer took it", disposition = "TakeNow", replacement = (object?)null });
        deliveryResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var pickupResponse = await Client.PostAsJsonAsync($"/api/pickups/{pickup.Id}/cancel",
            new { reason = "Customer took it", disposition = "TakeNow", replacement = (object?)null });
        pickupResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Nothing was cancelled or converted by the rejected calls.
        (await GetConversionsAsync(scene.SaleId)).Should().BeEmpty();
    }

    /// <summary>
    /// A disposition that builds a replacement creates a brand-new schedule, so it must clear the same
    /// sale-status gate the create endpoints enforce. Voiding a sale does not currently block it from
    /// having a still-Pending schedule, so without this guard a voided sale could gain a fresh Pending
    /// pickup through the cancel-with-conversion back door — one that
    /// <c>POST /api/sales/{id}/pickups</c> would have refused outright.
    /// <para>The whole attempt must be a no-op, not a partial one: same all-or-nothing guarantee as a
    /// failed replacement, reached from a different trigger.</para>
    /// </summary>
    [Fact]
    public async Task Cancelling_into_a_replacement_is_rejected_when_the_sale_was_voided()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/void", new VoidSaleRequest("Wrong customer")))
            .EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{delivery.Id}/cancel",
            new CancelDeliveryRequest("Customer will collect instead", CancellationDisposition.ConvertToPickup,
                new PickupReplacementInput(Today.AddDays(1), "Maria Santos", null, null)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>(TestJson.Options))!
            .Code.Should().Be(ErrorCodes.DeliveryReceiptNotAllowed);

        // Nothing was written: no replacement pickup, no conversion row, no intent movement.
        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(1);
            (await db.DeliveryReceipts.CountAsync(d =>
                d.SaleId == scene.SaleId && d.Method == FulfillmentMethod.Pickup)).Should().Be(0);
            return true;
        });
        (await GetConversionsAsync(scene.SaleId)).Should().BeEmpty();

        var intent = await GetIntentAsync(scene.SaleItemId);
        intent.Delivery.Should().Be(6m);
        intent.Pickup.Should().Be(0m);

        // And the delivery itself was not cancelled — the cancellation is part of the same transaction.
        var reloaded = await Client.GetFromJsonAsync<FulfillmentScheduleDto>(
            $"/api/delivery-receipts/{delivery.Id}", TestJson.Options);
        reloaded!.Status.Should().Be(FulfillmentStatus.Pending);
        reloaded.CancellationDisposition.Should().BeNull();
    }

    /// <summary>A release-only disposition creates nothing, so it stays permitted even on a voided sale —
    /// the released quantity can never be used, because creating a schedule against a voided sale is
    /// already blocked at the create endpoint.</summary>
    [Fact]
    public async Task Cancelling_with_a_release_only_disposition_still_works_on_a_voided_sale()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/void", new VoidSaleRequest("Wrong customer")))
            .EnsureSuccessStatusCode();

        var (status, body) = await CancelDeliveryAsync(delivery.Id, CancellationDisposition.DeliverLater, null);

        status.Should().Be(HttpStatusCode.OK);
        body!.Cancelled.Status.Should().Be(FulfillmentStatus.Cancelled);
        body.Replacement.Should().BeNull();
    }

    /// <summary>Spec test 22 — Delivered is terminal.</summary>
    [Fact]
    public async Task A_completed_delivery_cannot_be_cancelled()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{delivery.Id}/deliver", null)).EnsureSuccessStatusCode();

        var (status, _) = await CancelDeliveryAsync(delivery.Id, CancellationDisposition.DeliverLater, null);
        status.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Spec test 23 — Claimed is terminal: neither cancellable nor convertible.</summary>
    [Fact]
    public async Task A_claimed_pickup_can_neither_be_cancelled_nor_converted()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 6m);
        var pickup = await CreatePickupAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null)).EnsureSuccessStatusCode();

        (await CancelPickupAsync(pickup.Id, CancellationDisposition.PickupLater, null)).Status
            .Should().Be(HttpStatusCode.BadRequest);

        (await CancelPickupAsync(pickup.Id, CancellationDisposition.ConvertToDelivery,
            new DeliveryReplacementInput(Today.AddDays(1), "Maria", "88 Katipunan Ave", null, null))).Status
            .Should().Be(HttpStatusCode.BadRequest);

        (await GetConversionsAsync(scene.SaleId)).Should().BeEmpty();
    }

    /// <summary>Spec test 7 — claiming is a completion, never a cancellation. The two are different
    /// outcomes and a claimed pickup must carry no cancellation metadata at all.</summary>
    [Fact]
    public async Task Marking_a_pickup_claimed_does_not_cancel_it()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 6m);
        var pickup = await CreatePickupAsync(scene, 4m);

        var response = await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null);
        var claimed = (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        claimed.Status.Should().Be(FulfillmentStatus.Completed);
        claimed.CancelledAtUtc.Should().BeNull();
        claimed.CancellationDisposition.Should().BeNull();
        claimed.CancellationReason.Should().BeNull();

        (await GetConversionsAsync(scene.SaleId)).Should().BeEmpty(); // claiming is not a conversion
    }

    /// <summary>Spec tests 10 and 24 — a cancelled schedule is permanent history: still listed, still on
    /// the sale summary, and counted in no quantity bucket.</summary>
    [Fact]
    public async Task A_cancelled_schedule_stays_in_history_and_is_counted_nowhere()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);
        (await CancelDeliveryAsync(delivery.Id, CancellationDisposition.DeliverLater, null, "Wrong address")).Status
            .Should().Be(HttpStatusCode.OK);

        var list = await Client.GetFromJsonAsync<List<FulfillmentScheduleDto>>(
            $"/api/sales/{scene.SaleId}/delivery-receipts", TestJson.Options);
        var listed = list!.Should().ContainSingle(d => d.Id == delivery.Id).Subject;
        listed.Status.Should().Be(FulfillmentStatus.Cancelled);
        listed.Items.Should().ContainSingle(); // its own item rows are preserved as history

        var summary = await GetSummaryAsync(scene);
        summary!.Deliveries.Should().ContainSingle(d => d.Id == delivery.Id && d.Status == FulfillmentStatus.Cancelled);
        summary.Items[0].DeliveryPendingQuantity.Should().Be(0m);
        summary.Items[0].DeliveredQuantity.Should().Be(0m);
        summary.Items[0].DeliveryUnscheduledQuantity.Should().Be(6m);
    }

    /// <summary>Spec test 10's Pickup-side mirror of
    /// <c>A_cancelled_schedule_stays_in_history_and_is_counted_nowhere</c> above (which only exercises
    /// a cancelled Delivery). Pickup and Delivery share the same DeliveryReceipt entity and controller
    /// actions, but nothing before this test ever actually cancelled a Pickup and then re-fetched it —
    /// every other Pickup-cancellation test reads only the synchronous response body, never a
    /// subsequent GET/list/summary round trip. This proves the cancelled Pickup is real, persisted
    /// history: still listed by id, still present on the sale's fulfillment summary, and its released
    /// quantity is counted in no bucket other than pickup-unscheduled.</summary>
    [Fact]
    public async Task A_cancelled_pickup_stays_in_history_and_is_counted_nowhere()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 6m);
        var pickup = await CreatePickupAsync(scene, 4m);
        (await CancelPickupAsync(pickup.Id, CancellationDisposition.PickupLater, null, "No show")).Status
            .Should().Be(HttpStatusCode.OK);

        var reloaded = await Client.GetFromJsonAsync<FulfillmentScheduleDto>(
            $"/api/delivery-receipts/{pickup.Id}", TestJson.Options);
        reloaded!.Status.Should().Be(FulfillmentStatus.Cancelled);
        reloaded.Items.Should().ContainSingle(); // its own item rows are preserved as history

        var summary = await GetSummaryAsync(scene);
        summary!.Pickups.Should().ContainSingle(p => p.Id == pickup.Id && p.Status == FulfillmentStatus.Cancelled);
        summary.Items[0].PickupPendingQuantity.Should().Be(0m);
        summary.Items[0].ClaimedQuantity.Should().Be(0m);
        summary.Items[0].PickupUnscheduledQuantity.Should().Be(6m); // fully released, counted here only
    }

    /// <summary>Spec test 26 — a delivery charge belongs to the SALE, not to a schedule. Converting the
    /// last delivery into a pickup must not refund, zero or otherwise touch it; only the pickup's own DTO
    /// reports 0, because a pickup never carries a charge.</summary>
    [Fact]
    public async Task Converting_to_pickup_leaves_the_sale_delivery_charge_untouched()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m, deliveryCharge: 60m);
        var chargeBefore = await InScopeAsync(db => db.Sales.AsNoTracking()
            .Where(s => s.Id == scene.SaleId).Select(s => s.DeliveryCharge).SingleAsync());
        chargeBefore.Should().Be(60m);

        var delivery = await CreateDeliveryAsync(scene, 6m);
        var (status, body) = await CancelDeliveryAsync(
            delivery.Id, CancellationDisposition.ConvertToPickup,
            new PickupReplacementInput(Today.AddDays(1), "Maria Santos", null, null));
        status.Should().Be(HttpStatusCode.OK);

        var chargeAfter = await InScopeAsync(db => db.Sales.AsNoTracking()
            .Where(s => s.Id == scene.SaleId).Select(s => s.DeliveryCharge).SingleAsync());
        chargeAfter.Should().Be(chargeBefore);

        body!.Replacement!.DeliveryCharge.Should().Be(0m); // a pickup never reports one
        (await GetSummaryAsync(scene))!.DeliveryCharge.Should().Be(60m); // the sale still carries it
    }
}
