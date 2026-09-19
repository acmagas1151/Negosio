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

    private sealed record Scene(Guid SaleId, Guid SaleItemId, decimal Quantity, string Token);

    /// <summary>One completed sale whose single line is entirely earmarked for <paramref name="method"/> —
    /// whole-sale intent, per Task 1.</summary>
    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, FulfillmentMethod method = FulfillmentMethod.Delivery)
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
            Method: method));

        return new Scene(sale.SaleId, sale.Items[0].SaleItemId, qty, owner.AccessToken);
    }

    private static CreateDeliveryReceiptRequest DeliveryReq() => new(
        Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null);

    private static CreatePickupRequest PickupReq() => new(
        Today, "Juan Dela Cruz", "0917 111 2222", null);

    [Fact]
    public async Task Two_concurrent_creates_on_the_same_sale_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync();

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, DeliveryReq());

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        summary!.Deliveries.Should().ContainSingle(); // never duplicated
        summary.ActiveSchedule.Should().NotBeNull();
        summary.ActiveSchedule!.Status.Should().Be(FulfillmentStatus.Pending);
    }

    // Under the whole-sale model, a DeliverLater cancel creates its replacement Pending delivery
    // atomically as part of the cancel itself (Task 3B) — there is no longer an "unscheduled" window
    // between cancelling and rescheduling for two concurrent creates to race over. Both creates now
    // deterministically lose to the replacement that already exists.
    [Fact]
    public async Task Creating_after_a_DeliverLater_cancel_is_rejected_because_the_replacement_already_exists()
    {
        var scene = await ArrangeSaleAsync();
        var firstResponse = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", DeliveryReq());
        var first = (await firstResponse.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
        await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel",
            new CancelDeliveryRequest("Wrong address", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null)));

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, DeliveryReq());

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(2);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        summary!.Deliveries.Should().HaveCount(2); // the cancelled original + its replacement, never a third
        summary.ActiveSchedule.Should().NotBeNull();
        summary.ActiveSchedule!.Status.Should().Be(FulfillmentStatus.Pending);
    }

    [Fact]
    public async Task Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", DeliveryReq());
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
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", DeliveryReq());
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var deliverTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/deliver", scene.Token));
        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/cancel", scene.Token, new CancelDeliveryRequest("Race", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null))));

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
        var scene = await ArrangeSaleAsync(method: FulfillmentMethod.Pickup);
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq());
        var pickup = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var claimTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{pickup.Id}/claim", scene.Token));
        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/pickups/{pickup.Id}/cancel", scene.Token, new CancelPickupRequest("Race", CancellationDisposition.PickupLater, null,
                RescheduledPickup: new PickupReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "0917 111 2222", null))));

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

    // ================================================================================================
    // Task 11 — concurrency and idempotency coverage for the pickup and conversion paths.
    // (Adapted for Task 2 of the fulfillment-simplification plan: a sale now has at most one active
    // schedule of either method, so every "claiming the same last units" scenario below is re-expressed
    // as "only one of N concurrent creates on the same sale ever succeeds", rather than partial
    // over-allocation of a shared quantity pool.)
    // ================================================================================================

    // Scenario 1 (brief): concurrent pickup creation cannot double-book the same sale. The pickup-side
    // mirror of Two_concurrent_creates_on_the_same_sale_only_one_succeeds above — same UPDLOCK/HOLDLOCK
    // path, exercised through the pickup endpoints instead of the delivery ones.
    [Fact]
    public async Task Two_concurrent_pickup_creates_on_the_same_sale_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync(method: FulfillmentMethod.Pickup);

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/pickups", scene.Token, PickupReq());

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);

        var loser = results.Single(r => r.StatusCode == HttpStatusCode.BadRequest);
        var error = await loser.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be(Negosio.Application.Common.ErrorCodes.DeliveryReceiptNotAllowed);

        var summary = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>(
            $"/api/sales/{scene.SaleId}/fulfillment", TestJson.Options);
        summary!.Pickups.Should().ContainSingle(); // never duplicated
        summary.ActiveSchedule.Should().NotBeNull();
        summary.ActiveSchedule!.Status.Should().Be(FulfillmentStatus.Pending);
    }

    // Scenario 2 (brief): a conversion cannot race an allocation into double-booking the sale. Under the
    // simplified one-active-schedule invariant, a concurrent create can NEVER win this race regardless of
    // lock-acquisition order: if it wins the lock first, it sees DR1 still Pending and is rejected; if the
    // cancellation wins first, it replaces DR1 with a new Pending Pickup, and the create still sees an
    // active schedule and is rejected. This is the test Task 6's LockSaleItemsAsync fix (returning the
    // rows it just locked, so every allocating path reads intent from the POST-lock snapshot rather than
    // the pre-lock one on `sale`) exists to make correct — a buggy implementation that consulted a
    // pre-lock snapshot could still let the create wrongly see the sale as schedule-free.
    [Fact]
    public async Task A_cancel_with_conversion_never_loses_a_race_to_a_concurrent_create()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", DeliveryReq());
        var dr1 = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr1.Id}/cancel", scene.Token,
            new CancelDeliveryRequest("Address changed", CancellationDisposition.ConvertToPickup,
                new PickupReplacementInput(Today, "Juan Dela Cruz", "0917 111 2222", null))));
        var createTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, DeliveryReq()));

        var results = await Task.WhenAll(cancelTask, createTask);

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1); // the create can never win this race

        var saleItem = await InScopeAsync(db => db.SaleItems.AsNoTracking().SingleAsync(i => i.Id == scene.SaleItemId));
        (saleItem.DeliveryRequiredQuantity + saleItem.PickupRequiredQuantity).Should().Be(scene.Quantity); // conserved
        saleItem.DeliveryRequiredQuantity.Should().BeOneOf(0m, scene.Quantity);
        saleItem.PickupRequiredQuantity.Should().BeOneOf(0m, scene.Quantity);

        await InScopeAsync(async db =>
        {
            // Never more than one active schedule on the sale, however the race actually resolved.
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId && d.Status != FulfillmentStatus.Cancelled))
                .Should().Be(1);
            return true;
        });
    }

    // Scenario 3 (brief): two concurrent cancels of the same schedule. Which guard fires (the post-lock
    // ConflictException, or the pre-lock BusinessRuleException if the loser's very first read already
    // observes the winner's commit) depends on interleaving, so this deliberately does not assert an
    // exact status — only that not both succeeded and the loser got a non-2xx.
    [Fact]
    public async Task Two_concurrent_cancels_of_the_same_schedule_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", DeliveryReq());
        var dr = (await created.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;

        HttpRequestMessage CancelRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/cancel", scene.Token,
            new CancelDeliveryRequest("Race", CancellationDisposition.DeliverLater, null,
                RescheduledDelivery: new DeliveryReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null)));

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
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", DeliveryReq());
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
            saleItem.PickupRequiredQuantity.Should().Be(scene.Quantity);
            return true;
        });
    }

    // Scenario 9 (brief): N concurrent create-pickup requests against one sale must never surface an
    // unhandled 500, and — since a sale now has at most one active schedule of either method — exactly
    // one of them succeeds, with sequence number 1; every other request is cleanly rejected.
    [Fact]
    public async Task N_concurrent_pickup_creates_on_the_same_sale_only_one_ever_succeeds_and_never_surfaces_a_500()
    {
        const int concurrency = 8;
        var scene = await ArrangeSaleAsync(qty: 20m, method: FulfillmentMethod.Pickup);

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/pickups", scene.Token, PickupReq());

        var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Client.SendAsync(CreateRequest())));

        results.Should().NotContain(r => (int)r.StatusCode >= 500);
        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);

        var winner = results.Single(r => r.StatusCode == HttpStatusCode.Created);
        var createdDto = (await winner.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
        createdDto.SequenceNumber.Should().Be(1);

        var persistedCount = await InScopeAsync(db =>
            db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId && d.Method == FulfillmentMethod.Pickup));
        persistedCount.Should().Be(1);
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
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) },
            Method: FulfillmentMethod.Pickup));

        var tenantB = await RegisterLoginAndAuthorizeAsync(NewRegisterRequest(
            businessName: "Tenant B Co", email: "tenant-b@example.com", branchCode: "TB"));

        var response = await Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{saleA.SaleId}/pickups", tenantB.AccessToken, PickupReq()));

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
    // SaleItem outside its own sale (only the sale's own locked rows are ever used to build one), so
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

        // A second, unrelated sale purely to supply a genuine (FK-satisfying) but foreign SaleItemId.
        var sale2 = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 5m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) }));
        var foreignSaleItemId = sale2.Items[0].SaleItemId;

        var created = await Client.PostAsJsonAsync($"/api/sales/{sale1.SaleId}/delivery-receipts", DeliveryReq());
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
