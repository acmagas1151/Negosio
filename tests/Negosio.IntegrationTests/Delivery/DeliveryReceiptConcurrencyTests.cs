using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptConcurrencyTests : IntegrationTest
{
    public DeliveryReceiptConcurrencyTests(NegosioApiFactory factory) : base(factory) { }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    // Same helper as VoidConcurrencyTests.cs — explicit per-request Authorization header so two
    // concurrent requests never race on Client.DefaultRequestHeaders.
    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scene(Guid SaleId, Guid SaleItemId, string Token);

    /// <summary>
    /// One completed sale whose single line carries <paramref name="deliveryRequiredQuantity"/> of
    /// delivery intent (set directly at checkout, as before) and, when non-zero,
    /// <paramref name="pickupRequiredQuantity"/> of pickup intent — set via the same test-only raw-SQL
    /// backdoor <c>PickupTests</c> uses, since checkout cannot yet allocate a pickup quantity itself.
    /// Existing callers that never pass <paramref name="pickupRequiredQuantity"/> are unaffected: the
    /// backdoor is skipped entirely when it is zero, so the four pre-Task-11 tests below see no
    /// behavioral change.
    /// </summary>
    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, decimal deliveryRequiredQuantity = 4m, decimal pickupRequiredQuantity = 0m)
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
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) },
            Method: deliveryRequiredQuantity > 0m ? FulfillmentMethod.Delivery : FulfillmentMethod.TakeNow));

        var saleItemId = sale.Items[0].SaleItemId;
        if (pickupRequiredQuantity > 0m)
        {
            await SetFulfillmentIntentAsync(saleItemId, deliveryRequiredQuantity, pickupRequiredQuantity);
        }

        return new Scene(sale.SaleId, saleItemId, owner.AccessToken);
    }

    private static CreateDeliveryReceiptRequest FullyClaimingRequest(Scene s, decimal quantity) => new(
        Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
        new[] { new FulfillmentItemInput(s.SaleItemId, quantity) });

    private static CreatePickupRequest FullyClaimingPickupRequest(Scene s, decimal quantity) => new(
        Today, "Juan Dela Cruz", "0917 111 2222", null,
        new[] { new FulfillmentItemInput(s.SaleItemId, quantity) });

    [Fact]
    public async Task Two_concurrent_creates_claiming_the_same_last_units_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, FullyClaimingRequest(scene, 4m));

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        summary!.Items[0].DeliveryPendingQuantity.Should().Be(4m); // never over-allocated past what was ever delivery-required
        summary.Items[0].DeliveryUnscheduledQuantity.Should().Be(0m);
    }

    [Fact]
    public async Task Rescheduling_after_a_cancel_races_correctly_against_a_second_claim_of_the_released_quantity()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var firstResponse = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var first = (await firstResponse.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
        await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel", new CancelDeliveryRequest("Wrong address", CancellationDisposition.DeliverLater, null));

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, FullyClaimingRequest(scene, 4m));

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
    }

    [Fact]
    public async Task Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        HttpRequestMessage DeliverRequest() => AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/deliver", scene.Token);

        var results = await Task.WhenAll(Client.SendAsync(DeliverRequest()), Client.SendAsync(DeliverRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
    }

    // Scenario 7 (brief): cancel races mark-delivered. Exactly one wins, and the row ends in exactly ONE
    // of Cancelled or Completed — never a mix (e.g. cancelled but still carrying CompletedAtUtc from a
    // deliver that squeezed in). This test predates Task 11; the mix check below is Task 11's addition.
    [Fact]
    public async Task Concurrent_mark_delivered_and_cancel_on_the_same_delivery_never_both_succeed()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var deliverTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/deliver", scene.Token));
        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/cancel", scene.Token, new CancelDeliveryRequest("Race", CancellationDisposition.DeliverLater, null)));

        var results = await Task.WhenAll(deliverTask, cancelTask);

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);

        var final = await Client.GetFromJsonAsync<FulfillmentScheduleDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        final!.Status.Should().BeOneOf(FulfillmentStatus.Completed, FulfillmentStatus.Cancelled);

        // Never a mix: a Cancelled row must never also carry completion audit fields, and vice versa.
        if (final.Status == FulfillmentStatus.Cancelled)
        {
            final.CompletedAtUtc.Should().BeNull();
            final.CompletedByName.Should().BeNull();
        }
        else
        {
            final.CancelledAtUtc.Should().BeNull();
            final.CancelledByName.Should().BeNull();
            final.CancellationReason.Should().BeNull();
        }
    }

    // Scenario 8 (brief): the same race, on the pickup side — cancel vs. mark-claimed.
    [Fact]
    public async Task Concurrent_mark_claimed_and_cancel_on_the_same_pickup_never_both_succeed()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 4m);
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", FullyClaimingPickupRequest(scene, 4m));
        var pickup = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var claimTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{pickup.Id}/claim", scene.Token));
        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/pickups/{pickup.Id}/cancel", scene.Token, new CancelPickupRequest("Race", CancellationDisposition.PickupLater, null)));

        var results = await Task.WhenAll(claimTask, cancelTask);

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);

        var final = await Client.GetFromJsonAsync<FulfillmentScheduleDto>($"/api/delivery-receipts/{pickup.Id}", TestJson.Options);
        final!.Status.Should().BeOneOf(FulfillmentStatus.Completed, FulfillmentStatus.Cancelled);

        if (final.Status == FulfillmentStatus.Cancelled)
        {
            final.CompletedAtUtc.Should().BeNull();
            final.CompletedByName.Should().BeNull();
        }
        else
        {
            final.CancelledAtUtc.Should().BeNull();
            final.CancelledByName.Should().BeNull();
            final.CancellationReason.Should().BeNull();
        }
    }

    // Controller addendum (Task 9 review gap): the post-lock batch-idempotency re-check — added
    // mid-Task-9 because a unique index can't coexist with a batch writing N rows sharing one
    // BatchRequestId — has no test proving it survives a genuine concurrent race until this one.
    [Fact]
    public async Task Two_concurrent_batch_creates_with_the_same_BatchRequestId_only_one_set_of_rows_persists()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var batchId = Guid.NewGuid();
        var batchRequest = new CreateDeliveryReceiptBatchRequest(batchId, new[] { FullyClaimingRequest(scene, 4m) });

        HttpRequestMessage BatchRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts/batch", scene.Token, batchRequest);

        var results = await Task.WhenAll(Client.SendAsync(BatchRequest()), Client.SendAsync(BatchRequest()));

        // Both responses must succeed (idempotent — a retry is never an error) and describe the SAME
        // underlying batch, whether one raced ahead as the "real" creator or the other found it already
        // committed via the post-lock re-check.
        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
        var bodies = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options)));
        bodies[0]!.Created.Single().Id.Should().Be(bodies[1]!.Created.Single().Id);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(1); // never duplicated
            return true;
        });
    }

    // ================================================================================================
    // Task 11 — concurrency and idempotency coverage for the pickup and conversion paths.
    // ================================================================================================

    // Scenario 1 (brief): concurrent pickup creation cannot over-allocate. The pickup-side mirror of
    // Two_concurrent_creates_claiming_the_same_last_units_only_one_succeeds above — same UPDLOCK/HOLDLOCK
    // path, exercised through the pickup endpoints instead of the delivery ones.
    [Fact]
    public async Task Two_concurrent_pickup_creates_claiming_the_same_last_units_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 4m);

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/pickups", scene.Token, FullyClaimingPickupRequest(scene, 4m));

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);

        var loser = results.Single(r => r.StatusCode == HttpStatusCode.BadRequest);
        var error = await loser.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be(Negosio.Application.Common.ErrorCodes.PickupQuantityExceedsAvailable);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        summary!.Items[0].PickupPendingQuantity.Should().Be(4m); // never over-allocated past what was ever pickup-required
        summary.Items[0].PickupUnscheduledQuantity.Should().Be(0m);
    }

    // Scenario 2 (brief): a conversion cannot race an allocation. This is the test Task 6's
    // LockSaleItemsAsync fix (returning the rows it just locked, so every allocating path reads intent
    // from the POST-lock snapshot rather than the pre-lock one on `sale`) exists to make correct.
    //
    // Setup: DeliveryRequiredQuantity is set to exactly the quantity DR1 (the one Pending delivery)
    // already holds — there is deliberately no extra "unscheduled" headroom. That means a genuinely
    // correct implementation can NEVER let (b) succeed, in either lock-acquisition order:
    //   - if (b) wins the lock first, it reads DR1 still Pending, so Available = 4 - 4 = 0 -> rejected.
    //   - if (a) wins first, it cancels DR1 and converts its 4 units away, so DeliveryRequiredQuantity
    //     drops to 0 and Available = 0 - 0 = 0 -> rejected.
    // A buggy implementation that computes (b)'s availability from the pre-lock `sale.Items` snapshot
    // instead of the freshly-locked rows would, in the second ordering, still see the STALE
    // DeliveryRequiredQuantity of 4 combined with a FRESH (already-cancelled) pending sum of 0 -- i.e.
    // Available = 4, wrongly admitting a phantom second delivery for quantity that was simultaneously
    // moved to Pickup intent. That is exactly the race this test is designed to catch.
    [Fact]
    public async Task A_cancel_with_conversion_cannot_race_a_new_allocation_of_the_quantity_it_is_moving()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr1 = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr1.Id}/cancel", scene.Token,
            new CancelDeliveryRequest("Address changed", CancellationDisposition.ConvertToPickup,
                new PickupReplacementInput(Today, "Juan Dela Cruz", "0917 111 2222", null))));
        var createTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, FullyClaimingRequest(scene, 4m)));

        var results = await Task.WhenAll(cancelTask, createTask);

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);

        var saleItem = await InScopeAsync(db => db.SaleItems.AsNoTracking().SingleAsync(i => i.Id == scene.SaleItemId));
        (saleItem.DeliveryRequiredQuantity + saleItem.PickupRequiredQuantity).Should().Be(4m); // conserved, never exceeds Quantity
        saleItem.DeliveryRequiredQuantity.Should().BeOneOf(0m, 4m);
        saleItem.PickupRequiredQuantity.Should().BeOneOf(0m, 4m);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        var line = summary!.Items[0];
        // The real over-allocation check: what schedules actually hold must never exceed what the
        // intent columns currently say is theirs, on EITHER side of the conversion.
        (line.DeliveryPendingQuantity + line.DeliveredQuantity).Should().BeLessThanOrEqualTo(saleItem.DeliveryRequiredQuantity);
        (line.PickupPendingQuantity + line.ClaimedQuantity).Should().BeLessThanOrEqualTo(saleItem.PickupRequiredQuantity);
    }

    // Scenario 3 (brief): two concurrent cancels of the same schedule. Which guard fires (the post-lock
    // ConflictException, or the pre-lock BusinessRuleException if the loser's very first read already
    // observes the winner's commit) depends on interleaving, so this deliberately does not assert an
    // exact status — only that not both succeeded and the loser got a non-2xx.
    [Fact]
    public async Task Two_concurrent_cancels_of_the_same_schedule_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        HttpRequestMessage CancelRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/cancel", scene.Token,
            new CancelDeliveryRequest("Race", CancellationDisposition.DeliverLater, null));

        var results = await Task.WhenAll(Client.SendAsync(CancelRequest()), Client.SendAsync(CancelRequest()));

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1); // not both succeeded
        results.Should().Contain(r => !r.IsSuccessStatusCode); // the loser gets a non-2xx (409 or 400 — either is correct)

        var final = await Client.GetFromJsonAsync<FulfillmentScheduleDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        final!.Status.Should().Be(FulfillmentStatus.Cancelled);
    }

    // Scenario 4 (brief): a retried cancel-with-conversion must not double-apply. Deliberately sequential
    // (not concurrent) — "issue the same call twice" describes a client retrying after e.g. a dropped
    // response, not a true race. The second call's own status is irrelevant (the brief calls this out
    // explicitly); what matters is the END STATE: one conversion row, one replacement schedule, intent
    // moved exactly once. The unique index on FulfillmentConversions(SourceRecordId, SaleItemId) from
    // Task 4 is what backstops this.
    [Fact]
    public async Task Retrying_the_same_cancel_with_conversion_does_not_double_apply()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var cancelRequest = new CancelDeliveryRequest("Address changed", CancellationDisposition.ConvertToPickup,
            new PickupReplacementInput(Today, "Juan Dela Cruz", "0917 111 2222", null));

        var first = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", cancelRequest);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Retry the identical call. Not asserting its status on purpose.
        var second = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", cancelRequest);
        second.IsSuccessStatusCode.Should().BeFalse();

        await InScopeAsync(async db =>
        {
            (await db.FulfillmentConversions.CountAsync(c => c.SourceRecordId == dr.Id)).Should().Be(1);
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId && d.Method == FulfillmentMethod.Pickup)).Should().Be(1);

            var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(i => i.Id == scene.SaleItemId);
            saleItem.DeliveryRequiredQuantity.Should().Be(0m); // moved exactly once
            saleItem.PickupRequiredQuantity.Should().Be(4m);
            return true;
        });
    }

    // Scenario 5 (brief): pickup batch idempotency — same BatchRequestId twice creates the schedules
    // once and reports WasExistingBatch == true the second time.
    [Fact]
    public async Task Posting_the_same_pickup_batch_twice_creates_the_schedules_once()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 4m);
        var batchId = Guid.NewGuid();
        var batchRequest = new CreatePickupBatchRequest(batchId, new[] { FullyClaimingPickupRequest(scene, 4m) });

        var first = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups/batch", batchRequest);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = (await first.Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options))!;
        firstBody.WasExistingBatch.Should().BeFalse();

        var second = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups/batch", batchRequest);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondBody = (await second.Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options))!;
        secondBody.WasExistingBatch.Should().BeTrue();
        secondBody.Created.Single().Id.Should().Be(firstBody.Created.Single().Id);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId && d.Method == FulfillmentMethod.Pickup)).Should().Be(1);
            return true;
        });
    }

    // Scenario 6 (brief): batch idempotency is scoped to the sale — the same BatchRequestId against a
    // DIFFERENT sale must create new schedules, never return the first sale's rows.
    [Fact]
    public async Task The_same_BatchRequestId_against_a_different_sale_creates_new_schedules()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 100m);

        async Task<(Guid SaleId, Guid SaleItemId)> CheckoutWithPickupIntentAsync()
        {
            var sale = await CheckoutOkAsync(new CheckoutRequest(
                branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 10m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) }));
            var saleItemId = sale.Items[0].SaleItemId;
            await SetFulfillmentIntentAsync(saleItemId, 0m, 4m);
            return (sale.SaleId, saleItemId);
        }

        var (saleAId, saleItemAId) = await CheckoutWithPickupIntentAsync();
        var (saleBId, saleItemBId) = await CheckoutWithPickupIntentAsync();

        var batchId = Guid.NewGuid();
        var reqA = new CreatePickupBatchRequest(batchId, new[]
        {
            new CreatePickupRequest(Today, "Juan Dela Cruz", null, null, new[] { new FulfillmentItemInput(saleItemAId, 4m) })
        });
        var reqB = new CreatePickupBatchRequest(batchId, new[]
        {
            new CreatePickupRequest(Today, "Juan Dela Cruz", null, null, new[] { new FulfillmentItemInput(saleItemBId, 4m) })
        });

        var respA = await Client.PostAsJsonAsync($"/api/sales/{saleAId}/pickups/batch", reqA);
        respA.StatusCode.Should().Be(HttpStatusCode.Created);
        var bodyA = (await respA.Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options))!;
        bodyA.WasExistingBatch.Should().BeFalse();

        var respB = await Client.PostAsJsonAsync($"/api/sales/{saleBId}/pickups/batch", reqB);
        respB.StatusCode.Should().Be(HttpStatusCode.Created);
        var bodyB = (await respB.Content.ReadFromJsonAsync<FulfillmentBatchResultDto>(TestJson.Options))!;
        bodyB.WasExistingBatch.Should().BeFalse(); // NOT sale A's already-applied result
        bodyB.Created.Single().Id.Should().NotBe(bodyA.Created.Single().Id);
        bodyB.Created.Single().SaleId.Should().Be(saleBId);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.BatchRequestId == batchId)).Should().Be(2);
            return true;
        });
    }

    // Scenario 9 (brief): N concurrent create-pickup requests against one sale must never collide on
    // SequenceNumber, and a genuine (SaleId, Method, SequenceNumber) race must never surface as an
    // unhandled 500 — it has to be mapped to a clean conflict/business error.
    [Fact]
    public async Task N_concurrent_pickup_creates_never_collide_on_sequence_number()
    {
        const int concurrency = 8;
        // qty stays well within ArrangeSaleAsync's fixed 2000m tendered-cash amount (sellingPrice 100m x
        // qty 20m = 2000m) while still comfortably covering `concurrency` requests of 1 unit each.
        var scene = await ArrangeSaleAsync(qty: 20m, deliveryRequiredQuantity: 0m, pickupRequiredQuantity: 20m);

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/pickups", scene.Token, FullyClaimingPickupRequest(scene, 1m));

        var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Client.SendAsync(CreateRequest())));

        results.Should().NotContain(r => (int)r.StatusCode >= 500);

        var createdDtos = new List<FulfillmentScheduleDto>();
        foreach (var r in results.Where(r => r.StatusCode == HttpStatusCode.Created))
        {
            createdDtos.Add((await r.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!);
        }

        createdDtos.Select(d => d.SequenceNumber).Should().OnlyHaveUniqueItems();

        var persistedSequenceNumbers = await InScopeAsync(async db =>
            await db.DeliveryReceipts
                .Where(d => d.SaleId == scene.SaleId && d.Method == FulfillmentMethod.Pickup)
                .Select(d => d.SequenceNumber)
                .ToListAsync());
        persistedSequenceNumbers.Should().OnlyHaveUniqueItems();
        persistedSequenceNumbers.Count.Should().Be(createdDtos.Count);
    }

    // Scenario 10 (brief): cross-tenant items are rejected as 404 (not 403, not 500), and nothing is
    // created. Because tenancy here is database-per-tenant, tenant B's own connection simply has no row
    // for tenant A's SaleId at all — the ordinary SaleNotFound guard is what fires.
    [Fact]
    public async Task Scheduling_a_pickup_against_another_tenants_sale_is_rejected_as_not_found()
    {
        var tenantA = await RegisterLoginAndAuthorizeAsync(NewRegisterRequest(email: "tenant-a@example.com", branchCode: "TA"));
        var branchIdA = await GetMainBranchIdAsync(tenantA);
        var registerA = await CreateRegisterAsync(branchIdA);
        var sessionA = await OpenSessionAsync(registerA.Id);
        var categoryA = await CreateCategoryAsync();
        var (_, variantIdA) = await SeedStockedProductAsync(branchIdA, categoryA.Id, sellingPrice: 100m, openingStock: 20m);
        var saleA = await CheckoutOkAsync(new CheckoutRequest(
            branchIdA, sessionA.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantIdA, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) }));
        var saleItemIdA = saleA.Items[0].SaleItemId;
        await SetFulfillmentIntentAsync(saleItemIdA, 0m, 4m);

        var tenantB = await RegisterLoginAndAuthorizeAsync(NewRegisterRequest(
            businessName: "Tenant B Co", email: "tenant-b@example.com", branchCode: "TB"));

        var request = new CreatePickupRequest(Today, "Juan Dela Cruz", null, null,
            new[] { new FulfillmentItemInput(saleItemIdA, 4m) });

        var response = await Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{saleA.SaleId}/pickups", tenantB.AccessToken, request));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be(Negosio.Application.Common.ErrorCodes.SaleNotFound);

        await InTenantScopeAsync(tenantA.User.TenantId, async db =>
        {
            (await db.DeliveryReceipts.CountAsync()).Should().Be(0);
            return true;
        });
    }

    // Scenario 11 (brief) — "the single most important test in this task": a failed replacement rolls
    // back the whole cancellation. The API itself can never produce a schedule item pointing at a
    // SaleItem outside its own sale (ValidateAndResolveLines already rejects that at create time), so
    // this forces the corruption via a direct, test-only raw-SQL rewrite of the schedule's OWN item row
    // — simulating the exact shape of failure the brief specifies: a replacement item referencing a sale
    // item that does not belong to the sale. `foreignSaleItemId` is a genuine SaleItems row in this same
    // tenant database (satisfying the DeliveryReceiptItems -> SaleItems FK) that simply belongs to a
    // DIFFERENT sale, which is exactly what makes CancelWithDispositionAsync's
    // `saleItems.SingleOrDefault(...)` guard reject it — AFTER dr.Cancel() has already mutated the
    // change tracker, and after the replacement's items were already copied from dr.Items, but BEFORE
    // SaveChangesAsync ever runs. If the transaction boundary is real, none of that in-memory state ever
    // reaches the database.
    [Fact]
    public async Task A_failed_replacement_rolls_back_the_whole_cancellation()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 40m);

        var sale1 = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) },
            Method: FulfillmentMethod.Delivery));
        var saleItem1Id = sale1.Items[0].SaleItemId;

        // A second, unrelated sale purely to supply a genuine (FK-satisfying) but foreign SaleItemId.
        var sale2 = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 5m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) }));
        var foreignSaleItemId = sale2.Items[0].SaleItemId;

        var created = await Client.PostAsJsonAsync($"/api/sales/{sale1.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
                new[] { new FulfillmentItemInput(saleItem1Id, 4m) }));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var deliveryReceiptItemId = await InScopeAsync(db =>
            db.DeliveryReceiptItems.Where(i => i.DeliveryReceiptId == dr.Id).Select(i => i.Id).SingleAsync());
        await InScopeAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE DeliveryReceiptItems SET SaleItemId = {foreignSaleItemId} WHERE Id = {deliveryReceiptItemId}");
            return true;
        });

        var cancelResponse = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel",
            new CancelDeliveryRequest("Testing rollback", CancellationDisposition.ConvertToPickup,
                new PickupReplacementInput(Today, "Juan Dela Cruz", "0917 111 2222", null)));

        cancelResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await cancelResponse.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be(Negosio.Application.Common.ErrorCodes.InvalidSaleItem);

        await InScopeAsync(async db =>
        {
            var sourceStatus = await db.DeliveryReceipts.AsNoTracking()
                .Where(d => d.Id == dr.Id).Select(d => d.Status).SingleAsync();
            sourceStatus.Should().Be(FulfillmentStatus.Pending); // never actually cancelled in the database

            (await db.FulfillmentConversions.CountAsync(c => c.SourceRecordId == dr.Id)).Should().Be(0);
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == sale1.SaleId && d.Method == FulfillmentMethod.Pickup)).Should().Be(0);
            return true;
        });
    }
}
