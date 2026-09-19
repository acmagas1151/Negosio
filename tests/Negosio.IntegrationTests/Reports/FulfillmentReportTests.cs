using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Reports;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Reports;

/// <summary>
/// Task 10: the pickup report (mirrors the delivery report, Method == Pickup) and the combined
/// fulfillment view (one row per sale-item-per-method allocation, plus a ten-total summary block).
/// One Fact per scenario in the task brief's numbered list (11 total).
/// </summary>
public class FulfillmentReportTests : IntegrationTest
{
    public FulfillmentReportTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, Guid BranchId, Guid SaleItemId);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>Registers a fresh tenant, one branch, one open register session and one stocked product —
    /// everything a test needs before it can check out one or more sales against the same variant.</summary>
    private async Task<(Guid BranchId, Guid SessionId, Guid VariantId)> ArrangeTenantAsync(
        decimal price = 100m, decimal openingStock = 200m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: openingStock);
        return (branchId, session.Id, variantId);
    }

    /// <summary>One completed sale against an already-arranged tenant/branch/session/variant, whose single
    /// line carries the given delivery/pickup intent split; the remainder is take-now.</summary>
    private async Task<Scene> SellAsync(
        Guid branchId, Guid sessionId, Guid variantId, decimal qty,
        decimal deliveryRequiredQuantity = 0m, decimal pickupRequiredQuantity = 0m,
        decimal price = 100m, decimal deliveryCharge = 0m)
    {
        // NOTE (Task 1 of the whole-sale fulfillment simplification): CheckoutItemInput no longer
        // carries per-item delivery/pickup quantities — Method now applies to the whole sale. This
        // helper's deliveryRequiredQuantity/pickupRequiredQuantity parameters can no longer express
        // "some of each on one line"; mapped to the closest whole-sale equivalent below. This whole
        // file exercises the combined per-allocation report, which Task 4 of that plan deletes — full
        // rework of this file's scenarios (and its use of FulfillmentItemInput's partial quantities
        // below) is that task's job, not this one's.
        var method = deliveryRequiredQuantity > 0m ? FulfillmentMethod.Delivery
            : pickupRequiredQuantity > 0m ? FulfillmentMethod.Pickup
            : FulfillmentMethod.TakeNow;
        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, sessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + deliveryCharge + 1000m) },
            Method: method,
            DeliveryCharge: deliveryCharge));

        return new Scene(sale.SaleId, branchId, sale.Items[0].SaleItemId);
    }

    /// <summary>Convenience for the common case of exactly one sale in a brand-new tenant.</summary>
    private async Task<Scene> ArrangeSaleAsync(
        decimal qty = 10m, decimal deliveryRequiredQuantity = 0m, decimal pickupRequiredQuantity = 0m,
        decimal price = 100m, decimal deliveryCharge = 0m)
    {
        var (branchId, sessionId, variantId) = await ArrangeTenantAsync(price, qty + 20m);
        return await SellAsync(branchId, sessionId, variantId, qty, deliveryRequiredQuantity, pickupRequiredQuantity, price, deliveryCharge);
    }

    // NOTE (Task 2 of the whole-sale fulfillment simplification): CreateDeliveryReceiptRequest/
    // CreatePickupRequest no longer carry an Items list or a per-request quantity — a create now always
    // schedules 100% of whatever is currently earmarked for its method, and a sale has at most one
    // ACTIVE schedule (of either method) at a time. These two helpers are updated only enough to keep
    // this file compiling against the new contract shape; the `quantity` parameter is kept (but ignored)
    // so call sites below don't all need editing here. The scenarios in this file that create both a
    // Delivery AND a Pickup on the SAME sale, or multiple schedules on the same sale without an
    // intervening cancel, are no longer reachable under the simplified model — full rework of this
    // file's scenarios is Task 4's job (the combined per-allocation report), not this one's; see the
    // NOTE on SellAsync below for the same deferral this file already carried before Task 2.
    private async Task<FulfillmentScheduleDto> CreateDeliveryAsync(Scene s, decimal quantity, DateOnly? date = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(date ?? Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
    }

    private async Task<FulfillmentScheduleDto> CreatePickupAsync(Scene s, decimal quantity, DateOnly? date = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/pickups",
            new CreatePickupRequest(date ?? Today, "Juan Dela Cruz", "0917 111 2222", null));
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

    private Task BackdateScheduleDateAsync(Guid deliveryReceiptId, DateOnly scheduledDate) =>
        InScopeAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE DeliveryReceipts SET ScheduledDate = {scheduledDate} WHERE Id = {deliveryReceiptId}");
            return true;
        });

    // ---- Test 1 ----

    [Fact]
    public async Task Pickup_report_returns_only_pickup_schedules_a_sale_with_one_delivery_and_one_pickup_contributes_one_row()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 4m);
        await CreateDeliveryAsync(scene, 4m);
        await CreatePickupAsync(scene, 4m);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);

        pickupReport!.Page.Items.Should().ContainSingle(r => r.SaleId == scene.SaleId);
        pickupReport.Page.Items.Should().OnlyContain(r => r.SaleId == scene.SaleId); // no delivery row leaks in
    }

    // ---- Test 2 ----

    [Fact]
    public async Task Delivery_report_on_a_sale_with_one_delivery_and_one_pickup_returns_exactly_one_row()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 4m);
        await CreateDeliveryAsync(scene, 4m);
        await CreatePickupAsync(scene, 4m);

        var deliveryReport = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        deliveryReport!.Page.Items.Should().ContainSingle(r => r.SaleId == scene.SaleId); // the pickup never leaks in
    }

    // ---- Test 3 ----

    [Fact]
    public async Task Every_pickup_row_reports_zero_delivery_charge_even_when_the_sale_carries_one()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 4m, deliveryCharge: 60m);
        await CreateDeliveryAsync(scene, 4m);
        await CreatePickupAsync(scene, 4m);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);

        var row = pickupReport!.Page.Items.Should().ContainSingle(r => r.SaleId == scene.SaleId).Subject;
        row.DeliveryCharge.Should().Be(0m, "a pickup never carries a delivery charge, regardless of the sale's own charge");
    }

    // ---- Test 4: the double-counting regression guard ----

    [Fact]
    public async Task Combined_view_charges_a_sale_with_three_deliveries_and_two_pickups_exactly_once()
    {
        var scene = await ArrangeSaleAsync(qty: 20m, deliveryRequiredQuantity: 12m, pickupRequiredQuantity: 8m, deliveryCharge: 100m);

        await CreateDeliveryAsync(scene, 4m);
        await CreateDeliveryAsync(scene, 4m);
        await CreateDeliveryAsync(scene, 4m);
        await CreatePickupAsync(scene, 4m);
        await CreatePickupAsync(scene, 4m);

        var report = await Client.GetFromJsonAsync<FulfillmentReportResultDto>("/api/reports/fulfillment", TestJson.Options);

        report!.Summary.TotalDeliveryCharges.Should().Be(100m, "NOT 500m — charged once per sale, never per schedule");
    }

    // ---- Test 5: row-per-allocation shape ----

    [Fact]
    public async Task Combined_view_splits_one_line_into_a_row_per_method_and_status_and_quantities_sum_to_the_line()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 3m);
        var delivered = await CreateDeliveryAsync(scene, 2m);
        await Client.PostAsync($"/api/delivery-receipts/{delivered.Id}/deliver", null);
        await CreateDeliveryAsync(scene, 2m); // stays Pending
        // 3 units of pickup intent, deliberately never scheduled -> Pickup/Unscheduled.

        var report = await Client.GetFromJsonAsync<FulfillmentReportResultDto>("/api/reports/fulfillment", TestJson.Options);
        var rows = report!.Page.Items.Where(r => r.SaleItemId == scene.SaleItemId).ToList();

        rows.Should().HaveCount(4);
        rows.Should().ContainSingle(r => r.Method == FulfillmentMethod.TakeNow && r.Status == FulfillmentStatus.Completed && r.Quantity == 3m);
        rows.Should().ContainSingle(r => r.Method == FulfillmentMethod.Delivery && r.Status == FulfillmentStatus.Completed && r.Quantity == 2m);
        rows.Should().ContainSingle(r => r.Method == FulfillmentMethod.Delivery && r.Status == FulfillmentStatus.Pending && r.Quantity == 2m);
        rows.Should().ContainSingle(r => r.Method == FulfillmentMethod.Pickup && r.Status == FulfillmentStatus.Unscheduled && r.Quantity == 3m);
        rows.Sum(r => r.Quantity).Should().Be(10m);
    }

    // ---- Review fix: cancellation/conversion history rows, per the spec's explicit "source schedule;
    // replacement schedule; cancellation and conversion history" requirement for this report ----

    [Fact]
    public async Task Combined_view_shows_a_cancelled_schedule_as_its_own_row_linked_to_its_replacement_without_double_counting()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m); // 4 take-now
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var (status, body) = await CancelDeliveryAsync(
            delivery.Id, CancellationDisposition.ConvertToPickup,
            new PickupReplacementInput(Today.AddDays(1), "Maria Santos", null, null), "Customer will collect");
        status.Should().Be(HttpStatusCode.OK);
        var replacementPickupId = body!.Replacement!.Id;

        var report = await Client.GetFromJsonAsync<FulfillmentReportResultDto>("/api/reports/fulfillment", TestJson.Options);
        var rows = report!.Page.Items.Where(r => r.SaleItemId == scene.SaleItemId).ToList();

        // Two rows for this allocation: the cancelled delivery (history), linked FORWARD to what it
        // became, and the new pending pickup it produced (its own, ordinary row).
        var cancelledRow = rows.Should()
            .ContainSingle(r => r.Method == FulfillmentMethod.Delivery && r.Status == FulfillmentStatus.Cancelled).Subject;
        cancelledRow.Quantity.Should().Be(4m);
        cancelledRow.SourceScheduleId.Should().Be(delivery.Id);
        cancelledRow.ReplacementScheduleId.Should().Be(replacementPickupId);

        var pickupRow = rows.Should()
            .ContainSingle(r => r.Method == FulfillmentMethod.Pickup && r.Status == FulfillmentStatus.Pending).Subject;
        pickupRow.Quantity.Should().Be(4m);
        pickupRow.SourceScheduleId.Should().Be(replacementPickupId);

        // The cancelled row's 4 units must never be double-counted anywhere in the summary: the delivery
        // side shows only the 2 units still genuinely unscheduled (6 required - 4 converted away), never
        // the cancelled 4 as pending or unscheduled; the pickup side shows the 4 on the NEW pending
        // schedule, not duplicated. Summing every quantity bucket recovers exactly the sale's 10 units —
        // if the cancelled row had leaked into a total, this sum would read 14, not 10.
        var summary = report.Summary;
        summary.TotalDeliveryPendingQuantity.Should().Be(0m, "the only delivery schedule was cancelled, not left pending");
        summary.TotalDeliveryUnscheduledQuantity.Should().Be(2m, "6 required - 4 converted away = 2 still genuinely unscheduled");
        summary.TotalPickupPendingQuantity.Should().Be(4m, "the new pickup schedule, counted once");
        summary.TotalPickupUnscheduledQuantity.Should().Be(0m);
        summary.TotalCancelledSchedules.Should().Be(1);
        (summary.TotalTakeNowQuantity + summary.TotalDeliveryUnscheduledQuantity + summary.TotalDeliveryPendingQuantity
            + summary.TotalDeliveredQuantity + summary.TotalPickupUnscheduledQuantity + summary.TotalPickupPendingQuantity
            + summary.TotalClaimedQuantity).Should().Be(10m, "no bucket may double-count the cancelled schedule's quantity");
    }

    // ---- Test 6: the ten-total summary block, cross-checked against SaleFulfillmentCalculator.Derive ----

    [Fact]
    public async Task Combined_summary_totals_are_correct_and_sale_counts_match_the_calculator()
    {
        var (branchId, sessionId, variantId) = await ArrangeTenantAsync(price: 50m, openingStock: 200m);

        // Sale A: mixed delivery + pickup, fully fulfilled (take-now + delivered + claimed == sold).
        var sceneA = await SellAsync(branchId, sessionId, variantId, qty: 10m, deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 3m, price: 50m);
        var deliveryA = await CreateDeliveryAsync(sceneA, 4m);
        await Client.PostAsync($"/api/delivery-receipts/{deliveryA.Id}/deliver", null);
        var pickupA = await CreatePickupAsync(sceneA, 3m);
        await Client.PostAsync($"/api/delivery-receipts/{pickupA.Id}/claim", null);

        // Sale B: a delivery cancelled with DeliverLater (a plain release -> conversion event, quantity
        // goes back to delivery-unscheduled) and left otherwise unscheduled -> NeedsScheduling, not
        // NeedsAttention (nothing is overdue).
        var sceneB = await SellAsync(branchId, sessionId, variantId, qty: 6m, deliveryRequiredQuantity: 6m, price: 50m);
        var deliveryB = await CreateDeliveryAsync(sceneB, 6m);
        await CancelDeliveryAsync(deliveryB.Id, CancellationDisposition.DeliverLater, null, "Van broke down");

        // Sale C: an overdue pending delivery -> NeedsAttention.
        var sceneC = await SellAsync(branchId, sessionId, variantId, qty: 5m, deliveryRequiredQuantity: 5m, price: 50m);
        var deliveryC = await CreateDeliveryAsync(sceneC, 5m, Today.AddDays(3));
        await BackdateScheduleDateAsync(deliveryC.Id, Today.AddDays(-2));

        var report = await Client.GetFromJsonAsync<FulfillmentReportResultDto>("/api/reports/fulfillment", TestJson.Options);
        var summary = report!.Summary;

        // Sale A contributes: 3 take-now, 4 delivered, 3 claimed.
        // Sale B contributes: 6 delivery-unscheduled (released back), 1 cancelled schedule.
        // Sale C contributes: 5 delivery-pending.
        summary.TotalTakeNowQuantity.Should().Be(3m);
        summary.TotalDeliveredQuantity.Should().Be(4m);
        summary.TotalClaimedQuantity.Should().Be(3m);
        summary.TotalDeliveryUnscheduledQuantity.Should().Be(6m);
        summary.TotalDeliveryPendingQuantity.Should().Be(5m);
        summary.TotalPickupUnscheduledQuantity.Should().Be(0m);
        summary.TotalPickupPendingQuantity.Should().Be(0m);
        summary.TotalCancelledSchedules.Should().Be(1);

        summary.FullyFulfilledSalesCount.Should().Be(1, "only Sale A is fully fulfilled");
        summary.SalesNeedingAttentionCount.Should().Be(1, "only Sale C has an overdue pending schedule");

        // Cross-check against the calculator directly (same call the report itself makes), so the two
        // can never silently disagree.
        var summaryA = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>($"/api/sales/{sceneA.SaleId}/fulfillment", TestJson.Options);
        summaryA!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.Fulfilled);
        var summaryC = await Client.GetFromJsonAsync<SaleFulfillmentSummaryDto>($"/api/sales/{sceneC.SaleId}/fulfillment", TestJson.Options);
        summaryC!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.NeedsAttention);
    }

    // ---- Test 7 (spec test 29) ----

    [Fact]
    public async Task Delivery_cancelled_via_CustomerPickedUpInstead_still_reports_Cancelled_in_the_delivery_view()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var (status, _) = await CancelDeliveryAsync(
            delivery.Id, CancellationDisposition.CustomerPickedUpInstead,
            new PickupReplacementInput(Today, "Juan Dela Cruz", null, null), "Walked in and took it");
        status.Should().Be(HttpStatusCode.OK);

        var deliveryReport = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        var deliveryRow = deliveryReport!.Page.Items.Should().ContainSingle(r => r.DeliveryReceiptId == delivery.Id).Subject;
        deliveryRow.Status.Should().Be(FulfillmentStatus.Cancelled);
        deliveryRow.Status.Should().NotBe(FulfillmentStatus.Completed, "a delivery that was actually picked up must never read as a successful delivery");
    }

    // ---- Test 8 (spec test 30) ----

    [Fact]
    public async Task Pickup_created_by_CustomerPickedUpInstead_reports_Claimed_in_the_pickup_view()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var delivery = await CreateDeliveryAsync(scene, 4m);

        var (status, body) = await CancelDeliveryAsync(
            delivery.Id, CancellationDisposition.CustomerPickedUpInstead,
            new PickupReplacementInput(Today, "Juan Dela Cruz", null, null), "Walked in and took it");
        status.Should().Be(HttpStatusCode.OK);
        var replacementPickupId = body!.Replacement!.Id;

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);

        var pickupRow = pickupReport!.Page.Items.Should().ContainSingle(r => r.DeliveryReceiptId == replacementPickupId).Subject;
        pickupRow.Status.Should().Be(FulfillmentStatus.Completed, "the replacement pickup is created already-Claimed");
    }

    // ---- Test 9: authorization ----

    [Fact]
    public async Task All_three_endpoints_require_the_ReportsView_policy()
    {
        var scene = await ArrangeSaleAsync();
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        Authorize(cashierToken);

        (await Client.GetAsync("/api/reports/pickup")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.GetAsync("/api/reports/fulfillment")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.GetAsync("/api/reports/deliveries")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Test 10: branch scoping ----

    [Fact]
    public async Task Branch_scoped_user_sees_only_their_own_branchs_rows_in_all_three_views()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var mainRegister = await CreateRegisterAsync(mainId, "M1", "M1");
        var mainSession = await OpenSessionAsync(mainRegister.Id);
        var category = await CreateCategoryAsync();
        var (_, mainVariantId) = await SeedStockedProductAsync(mainId, category.Id, sku: "MAIN-SKU", sellingPrice: 100m, openingStock: 30m);
        var mainSale = await CheckoutOkAsync(new CheckoutRequest(
            mainId, mainSession.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(mainVariantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1500m) },
            Method: FulfillmentMethod.Delivery));
        var mainScene = new Scene(mainSale.SaleId, mainId, mainSale.Items[0].SaleItemId);
        await CreateDeliveryAsync(mainScene, 4m);
        await CreatePickupAsync(mainScene, 4m);

        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/close", new CloseRegisterSessionRequest(1200m)))
            .EnsureSuccessStatusCode();

        var bgcRegister = await CreateRegisterAsync(bgc.Id, "B1", "B1");
        var bgcSession = await OpenSessionAsync(bgcRegister.Id);
        var (_, bgcVariantId) = await SeedStockedProductAsync(bgc.Id, category.Id, sku: "BGC-SKU", sellingPrice: 150m, openingStock: 30m);
        var bgcSale = await CheckoutOkAsync(new CheckoutRequest(
            bgc.Id, bgcSession.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(bgcVariantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) },
            Method: FulfillmentMethod.Delivery));
        var bgcScene = new Scene(bgcSale.SaleId, bgc.Id, bgcSale.Items[0].SaleItemId);
        await CreateDeliveryAsync(bgcScene, 4m);
        await CreatePickupAsync(bgcScene, 4m);

        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainId);
        Authorize(managerToken);

        var deliveryReport = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        deliveryReport!.Page.Items.Should().OnlyContain(r => r.SaleId == mainSale.SaleId);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);
        pickupReport!.Page.Items.Should().OnlyContain(r => r.SaleId == mainSale.SaleId);

        var fulfillmentReport = await Client.GetFromJsonAsync<FulfillmentReportResultDto>("/api/reports/fulfillment", TestJson.Options);
        fulfillmentReport!.Page.Items.Should().OnlyContain(r => r.SaleId == mainSale.SaleId);

        // Even explicitly requesting the other branch must not leak BGC's rows to the Main manager.
        var attemptBgc = await Client.GetFromJsonAsync<FulfillmentReportResultDto>(
            $"/api/reports/fulfillment?branchId={bgc.Id}", TestJson.Options);
        attemptBgc!.Page.Items.Should().OnlyContain(r => r.SaleId == mainSale.SaleId);
    }

    // ---- Test 11: tenant isolation ----

    [Fact]
    public async Task A_second_tenants_sales_never_appear_in_any_of_the_three_views()
    {
        // Tenant 1: arrange directly (not via ArrangeSaleAsync) so its login token can be kept for
        // re-authorizing after switching to tenant 2 below.
        var owner1 = await RegisterLoginAndAuthorizeAsync();
        var branch1 = await GetMainBranchIdAsync(owner1);
        var register1 = await CreateRegisterAsync(branch1);
        var session1 = await OpenSessionAsync(register1.Id);
        var category1 = await CreateCategoryAsync();
        var (_, variant1) = await SeedStockedProductAsync(branch1, category1.Id, sku: "T1-SKU", sellingPrice: 100m, openingStock: 20m);
        var sale1 = await CheckoutOkAsync(new CheckoutRequest(
            branch1, session1.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variant1, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1500m) },
            Method: FulfillmentMethod.Delivery));
        var scene1 = new Scene(sale1.SaleId, branch1, sale1.Items[0].SaleItemId);
        await CreateDeliveryAsync(scene1, 4m);
        await CreatePickupAsync(scene1, 4m);

        // Tenant 2: a second, entirely separate tenant with its own delivery + pickup schedules.
        var owner2 = await RegisterLoginAndAuthorizeAsync(NewRegisterRequest(
            businessName: "Other Biz", branchCode: "OTHER", email: "other-owner@example.com"));
        var branch2 = await GetMainBranchIdAsync(owner2);
        var register2 = await CreateRegisterAsync(branch2);
        var session2 = await OpenSessionAsync(register2.Id);
        var category2 = await CreateCategoryAsync();
        var (_, variant2) = await SeedStockedProductAsync(branch2, category2.Id, sku: "T2-SKU", sellingPrice: 100m, openingStock: 20m);
        var sale2 = await CheckoutOkAsync(new CheckoutRequest(
            branch2, session2.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variant2, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1500m) },
            Method: FulfillmentMethod.Delivery));
        var scene2 = new Scene(sale2.SaleId, branch2, sale2.Items[0].SaleItemId);
        await CreateDeliveryAsync(scene2, 4m);
        await CreatePickupAsync(scene2, 4m);

        // Switch back to tenant 1: its own rows must still be visible, but tenant 2's must never appear.
        Authorize(owner1.AccessToken);

        var deliveryReport = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        deliveryReport!.Page.Items.Should().ContainSingle(r => r.SaleId == sale1.SaleId);
        deliveryReport.Page.Items.Should().NotContain(r => r.SaleId == sale2.SaleId);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);
        pickupReport!.Page.Items.Should().ContainSingle(r => r.SaleId == sale1.SaleId);
        pickupReport.Page.Items.Should().NotContain(r => r.SaleId == sale2.SaleId);

        var fulfillmentReport = await Client.GetFromJsonAsync<FulfillmentReportResultDto>("/api/reports/fulfillment", TestJson.Options);
        fulfillmentReport!.Page.Items.Should().Contain(r => r.SaleId == sale1.SaleId);
        fulfillmentReport.Page.Items.Should().NotContain(r => r.SaleId == sale2.SaleId);
    }
}
