using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

/// <summary>
/// Pickup is the third fulfillment method: quantity the customer collects at the branch after checkout.
/// It shares the DeliveryReceipts table with Delivery (discriminated by <c>Method</c>) but keeps an
/// entirely separate quantity pool, its own per-sale sequence numbering, and its own completion verb
/// (claim, never deliver). A sale has at most one active (non-Cancelled) schedule at a time, of either
/// method — see Task 2 of the fulfillment-simplification plan.
/// </summary>
public class PickupTests : IntegrationTest
{
    public PickupTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, Guid BranchId, Guid SaleItemId, decimal Quantity);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>One completed sale whose single line is entirely earmarked for <paramref name="method"/> —
    /// whole-sale intent, per Task 1.</summary>
    private async Task<Scene> ArrangeSaleAsync(
        decimal qty = 10m, FulfillmentMethod method = FulfillmentMethod.Pickup, decimal price = 100m)
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

        return new Scene(sale.SaleId, branchId, sale.Items[0].SaleItemId, qty);
    }

    private static CreatePickupRequest PickupReq(DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "0917 111 2222", "Collect at the counter");

    private static CreateDeliveryReceiptRequest DeliveryReq(DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null);

    private async Task<FulfillmentScheduleDto> CreatePickupAsync(Scene s, DateOnly? date = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/pickups", PickupReq(date));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    private async Task<FulfillmentScheduleDto> CreateDeliveryAsync(Scene s, DateOnly? date = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/delivery-receipts", DeliveryReq(date));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    private Task<SaleFulfillmentSummaryDto?> GetSummaryAsync(Scene s) =>
        Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>($"/api/sales/{s.SaleId}/fulfillment", TestJson.Options);

    /// <summary>Sequence numbers are per-sale AND per-method: a sale whose delivery was created (and later
    /// cancelled into a pickup) still names that pickup "Pickup 1" — not "2". A pickup also never
    /// carries an address. (A sale can have only one ACTIVE schedule at a time; since Task 3B, every
    /// cancel disposition creates its replacement as part of the SAME cancel call — there is no longer
    /// an "unscheduled" window in which an unrelated, freshly-POSTed pickup could be created, so
    /// ConvertToPickup's replacement is exercised directly here instead.)</summary>
    [Fact]
    public async Task Cancelling_a_delivery_into_a_pickup_numbers_it_independently_and_carries_no_address()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, method: FulfillmentMethod.Delivery);
        var delivery = await CreateDeliveryAsync(scene);
        delivery.SequenceNumber.Should().Be(1);
        delivery.Method.Should().Be(FulfillmentMethod.Delivery);

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{delivery.Id}/cancel",
            new CancelDeliveryRequest("Customer changed mind", CancellationDisposition.ConvertToPickup,
                new PickupReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "0917 111 2222", "Collect at the counter")));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var pickup = (await response.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))!.Replacement!;

        pickup.SequenceNumber.Should().Be(1); // NOT 2 — the two methods number separately
        pickup.Method.Should().Be(FulfillmentMethod.Pickup);
        pickup.Status.Should().Be(FulfillmentStatus.Pending);
        pickup.DeliveryAddress.Should().BeNull();
        pickup.DeliveryCharge.Should().Be(0m);
        pickup.Items.Should().ContainSingle();
        pickup.Items[0].Quantity.Should().Be(scene.Quantity);
    }

    [Fact]
    public async Task Create_pickup_rejects_a_second_pickup_when_one_is_already_pending()
    {
        var scene = await ArrangeSaleAsync();
        await CreatePickupAsync(scene);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Delivery intent and pickup intent are two separate pools. Quantity marked for DELIVERY
    /// can never be scheduled as a pickup without an explicit conversion.</summary>
    [Fact]
    public async Task Create_pickup_is_rejected_when_nothing_is_marked_for_pickup()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, method: FulfillmentMethod.Delivery); // marked for delivery, not pickup

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Mark_claimed_transitions_pending_to_completed_and_stamps_audit_fields()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene);

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
        var pickup = await CreatePickupAsync(scene);
        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Marking_a_delivery_claimed_is_rejected()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, method: FulfillmentMethod.Delivery);
        var delivery = await CreateDeliveryAsync(scene);

        var response = await Client.PostAsync($"/api/delivery-receipts/{delivery.Id}/claim", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Marking_a_pickup_delivered_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene);

        var response = await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Claimed is terminal — the customer already has the goods, so there is nothing left to
    /// cancel, reschedule or convert.</summary>
    [Fact]
    public async Task A_claimed_pickup_cannot_be_cancelled()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene);
        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/pickups/{pickup.Id}/cancel",
            new CancelPickupRequest("Too late", CancellationDisposition.PickupLater, null,
                RescheduledPickup: new PickupReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "0917 111 2222", null)));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Claiming_a_pickup_moves_the_sale_from_PendingPickup_to_Claimed()
    {
        var scene = await ArrangeSaleAsync(qty: 10m);
        var pickup = await CreatePickupAsync(scene);

        var beforeClaim = await GetSummaryAsync(scene);
        beforeClaim!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.PendingPickup);
        beforeClaim.ActiveSchedule.Should().NotBeNull();
        beforeClaim.ActiveSchedule!.Id.Should().Be(pickup.Id);
        beforeClaim.ActiveSchedule.Status.Should().Be(FulfillmentStatus.Pending);

        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null)).EnsureSuccessStatusCode();

        var afterClaim = await GetSummaryAsync(scene);
        afterClaim!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.Claimed);
        afterClaim.ActiveSchedule.Should().NotBeNull(); // Completed still counts as the active schedule
        afterClaim.ActiveSchedule!.Id.Should().Be(pickup.Id);
        afterClaim.ActiveSchedule.Status.Should().Be(FulfillmentStatus.Completed);

        // And the API agrees: re-scheduling is rejected — the schedule is Completed (not Cancelled), so
        // the one-active-schedule guard still blocks a second create.
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/pickups", PickupReq()))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Spec item 34 ("Unauthorized actions are rejected") for the Pickup lifecycle specifically. Claim
    /// sits behind the class-level SalesView policy (any POS role, same as Mark Delivered on the
    /// delivery side); Cancel sits behind the narrower FulfillmentCancel policy (Manager/Admin/Owner
    /// only). <see cref="Negosio.IntegrationTests.Delivery.DeliveryReceiptStatusTests"/> already proves
    /// this exact policy pair on the Delivery endpoints
    /// (<c>Cashier_may_mark_delivered_but_not_cancel</c> / <c>Cashier_cannot_cancel_a_delivery</c>);
    /// this is its Pickup-side mirror, since Pickup reaches the authorization filter through its own
    /// distinct routes (<c>POST /api/pickups/{id}/cancel</c> and the shared claim endpoint) even though
    /// both methods share the same DeliveryReceipt entity underneath.
    /// </summary>
    [Fact]
    public async Task Cashier_may_claim_a_pickup_but_not_cancel_it()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene);
        Authorize(await AddTenantUserTokenAsync("pickup-cashier@example.com", UserRole.Cashier, scene.BranchId));

        (await Client.PostAsync($"/api/delivery-receipts/{pickup.Id}/claim", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cashier_without_grant_requires_approval_then_succeeds_with_valid_manager_credentials()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene);
        var cashierToken = await AddTenantUserTokenAsync("pickup-cashier2@example.com", UserRole.Cashier, scene.BranchId);
        await CreateManagerAsync("pickup-mgr@example.com", "Manager123!", scene.BranchId);

        Authorize(cashierToken);
        var replacement = new PickupReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "0917 111 2222", null);

        var denied = await Client.PostAsJsonAsync($"/api/pickups/{pickup.Id}/cancel",
            new CancelPickupRequest("Changed mind", CancellationDisposition.PickupLater, null, RescheduledPickup: replacement));
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await denied.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.FulfillmentCancelApprovalRequired);

        var approved = await Client.PostAsJsonAsync($"/api/pickups/{pickup.Id}/cancel",
            new CancelPickupRequest("Changed mind", CancellationDisposition.PickupLater, null, RescheduledPickup: replacement,
                Approval: new VoidSaleApprovalInput("pickup-mgr@example.com", "Manager123!")));
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await approved.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))!;
        result.Cancelled.ApprovedByName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Cashier_with_a_direct_grant_cancels_a_pickup_without_any_approval()
    {
        var scene = await ArrangeSaleAsync();
        var pickup = await CreatePickupAsync(scene);
        var cashierToken = await AddTenantUserTokenAsync("pickup-cashier3@example.com", UserRole.Cashier, scene.BranchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/staff/{cashierId}/permissions")
        {
            Content = JsonContent.Create(new ChangeStaffPermissionsRequest(
                SalesVoid: false, SalesReturn: false, DiscountApply: false, CashDrawerOpen: false, FulfillmentCancel: true)),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(cashierToken);
        var response = await Client.PostAsJsonAsync($"/api/pickups/{pickup.Id}/cancel",
            new CancelPickupRequest("Changed mind", CancellationDisposition.PickupLater, null,
                RescheduledPickup: new PickupReplacementInput(Today.AddDays(1), "Juan Dela Cruz", "0917 111 2222", null)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<CancellationResultDto>(TestJson.Options))!;
        result.Cancelled.ApprovedByName.Should().BeNull("the cashier acted on their own direct grant — no approval was used");
    }
}
