using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Reports;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Reports;

/// <summary>
/// Task 4 of the whole-sale fulfillment simplification: the Pickup report (mirrors the Delivery report,
/// Method == Pickup) plus cross-cutting scenarios (authorization, branch scoping, tenant isolation) shared
/// by both the Delivery and Pickup report endpoints. The combined per-allocation "Fulfillment" report this
/// file used to also cover (one row per sale-item-per-method-per-status allocation, plus a ten-total
/// summary) is deleted by this task — Task 2's one-active-schedule-per-sale invariant means that concept
/// (a sale item split across several simultaneous allocations) no longer exists, so the scenarios that
/// exercised it are deleted rather than patched, per the plan's spec: only a Delivery report, a Pickup
/// report, and a simplified Sale-level status remain.
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

    /// <summary>One completed sale against an already-arranged tenant/branch/session/variant. A sale now
    /// has whole-sale <see cref="FulfillmentMethod"/> (Task 1) — <paramref name="deliveryRequiredQuantity"/>/
    /// <paramref name="pickupRequiredQuantity"/> are kept only to pick which single method the checkout
    /// uses (Delivery if the former is positive, Pickup if the latter is, otherwise TakeNow); passing both
    /// positive no longer produces a mixed sale, since Method now applies to the whole sale.</summary>
    private async Task<Scene> SellAsync(
        Guid branchId, Guid sessionId, Guid variantId, decimal qty,
        decimal deliveryRequiredQuantity = 0m, decimal pickupRequiredQuantity = 0m,
        decimal price = 100m, decimal deliveryCharge = 0m)
    {
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

    /// <summary>A create now always schedules 100% of whatever is currently earmarked for its method, and
    /// a sale has at most one ACTIVE schedule (of either method) at a time — the <c>quantity</c> parameter
    /// is kept (but ignored) so call sites below don't need editing.</summary>
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

    // ---- Pickup/Delivery report cross-leakage ----

    [Fact]
    public async Task Pickup_report_never_leaks_a_delivery_schedule_from_a_different_sale()
    {
        var (branchId, sessionId, variantId) = await ArrangeTenantAsync();
        var deliveryScene = await SellAsync(branchId, sessionId, variantId, qty: 10m, deliveryRequiredQuantity: 10m);
        await CreateDeliveryAsync(deliveryScene, 10m);
        var pickupScene = await SellAsync(branchId, sessionId, variantId, qty: 10m, pickupRequiredQuantity: 10m);
        await CreatePickupAsync(pickupScene, 10m);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);

        pickupReport!.Page.Items.Should().ContainSingle(r => r.SaleId == pickupScene.SaleId);
        pickupReport.Page.Items.Should().NotContain(r => r.SaleId == deliveryScene.SaleId);
    }

    [Fact]
    public async Task Delivery_report_never_leaks_a_pickup_schedule_from_a_different_sale()
    {
        var (branchId, sessionId, variantId) = await ArrangeTenantAsync();
        var deliveryScene = await SellAsync(branchId, sessionId, variantId, qty: 10m, deliveryRequiredQuantity: 10m);
        await CreateDeliveryAsync(deliveryScene, 10m);
        var pickupScene = await SellAsync(branchId, sessionId, variantId, qty: 10m, pickupRequiredQuantity: 10m);
        await CreatePickupAsync(pickupScene, 10m);

        var deliveryReport = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        deliveryReport!.Page.Items.Should().ContainSingle(r => r.SaleId == deliveryScene.SaleId);
        deliveryReport.Page.Items.Should().NotContain(r => r.SaleId == pickupScene.SaleId);
    }

    [Fact]
    public async Task Every_pickup_row_reports_zero_delivery_charge()
    {
        // A pickup-method sale's own DeliveryCharge is always 0 (validated server-side — Task 1's Global
        // Constraints), so this guards the report row's own hardcoded 0m rather than a Sale-side charge.
        var scene = await ArrangeSaleAsync(qty: 10m, pickupRequiredQuantity: 10m);
        await CreatePickupAsync(scene, 10m);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);

        var row = pickupReport!.Page.Items.Should().ContainSingle(r => r.SaleId == scene.SaleId).Subject;
        row.DeliveryCharge.Should().Be(0m, "a pickup never carries a delivery charge");
    }

    // ---- Cancellation disposition reporting ----

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

    // ---- Authorization ----

    [Fact]
    public async Task Both_report_endpoints_require_the_ReportsView_policy()
    {
        var scene = await ArrangeSaleAsync();
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        Authorize(cashierToken);

        (await Client.GetAsync("/api/reports/pickup")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.GetAsync("/api/reports/deliveries")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Branch scoping ----

    [Fact]
    public async Task Branch_scoped_user_sees_only_their_own_branchs_rows_in_both_views()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var mainRegister = await CreateRegisterAsync(mainId, "M1", "M1");
        var mainSession = await OpenSessionAsync(mainRegister.Id);
        var category = await CreateCategoryAsync();
        var (_, mainVariantId) = await SeedStockedProductAsync(mainId, category.Id, sku: "MAIN-SKU", sellingPrice: 100m, openingStock: 30m);

        var mainDeliveryScene = await SellAsync(mainId, mainSession.Id, mainVariantId, qty: 4m, deliveryRequiredQuantity: 4m);
        await CreateDeliveryAsync(mainDeliveryScene, 4m);
        var mainPickupScene = await SellAsync(mainId, mainSession.Id, mainVariantId, qty: 4m, pickupRequiredQuantity: 4m);
        await CreatePickupAsync(mainPickupScene, 4m);

        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/close", new CloseRegisterSessionRequest(1200m)))
            .EnsureSuccessStatusCode();

        var bgcRegister = await CreateRegisterAsync(bgc.Id, "B1", "B1");
        var bgcSession = await OpenSessionAsync(bgcRegister.Id);
        var (_, bgcVariantId) = await SeedStockedProductAsync(bgc.Id, category.Id, sku: "BGC-SKU", sellingPrice: 150m, openingStock: 30m);

        var bgcDeliveryScene = await SellAsync(bgc.Id, bgcSession.Id, bgcVariantId, qty: 4m, deliveryRequiredQuantity: 4m);
        await CreateDeliveryAsync(bgcDeliveryScene, 4m);
        var bgcPickupScene = await SellAsync(bgc.Id, bgcSession.Id, bgcVariantId, qty: 4m, pickupRequiredQuantity: 4m);
        await CreatePickupAsync(bgcPickupScene, 4m);

        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainId);
        Authorize(managerToken);

        var deliveryReport = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        deliveryReport!.Page.Items.Should().OnlyContain(r => r.SaleId == mainDeliveryScene.SaleId);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);
        pickupReport!.Page.Items.Should().OnlyContain(r => r.SaleId == mainPickupScene.SaleId);
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task A_second_tenants_sales_never_appear_in_either_view()
    {
        // Tenant 1: arrange directly (not via ArrangeSaleAsync) so its login token can be kept for
        // re-authorizing after switching to tenant 2 below.
        var owner1 = await RegisterLoginAndAuthorizeAsync();
        var branch1 = await GetMainBranchIdAsync(owner1);
        var register1 = await CreateRegisterAsync(branch1);
        var session1 = await OpenSessionAsync(register1.Id);
        var category1 = await CreateCategoryAsync();
        var (_, variant1) = await SeedStockedProductAsync(branch1, category1.Id, sku: "T1-SKU", sellingPrice: 100m, openingStock: 20m);

        var deliveryScene1 = await SellAsync(branch1, session1.Id, variant1, qty: 4m, deliveryRequiredQuantity: 4m);
        await CreateDeliveryAsync(deliveryScene1, 4m);
        var pickupScene1 = await SellAsync(branch1, session1.Id, variant1, qty: 4m, pickupRequiredQuantity: 4m);
        await CreatePickupAsync(pickupScene1, 4m);

        // Tenant 2: a second, entirely separate tenant with its own delivery + pickup schedules.
        var owner2 = await RegisterLoginAndAuthorizeAsync(NewRegisterRequest(
            businessName: "Other Biz", branchCode: "OTHER", email: "other-owner@example.com"));
        var branch2 = await GetMainBranchIdAsync(owner2);
        var register2 = await CreateRegisterAsync(branch2);
        var session2 = await OpenSessionAsync(register2.Id);
        var category2 = await CreateCategoryAsync();
        var (_, variant2) = await SeedStockedProductAsync(branch2, category2.Id, sku: "T2-SKU", sellingPrice: 100m, openingStock: 20m);

        var deliveryScene2 = await SellAsync(branch2, session2.Id, variant2, qty: 4m, deliveryRequiredQuantity: 4m);
        await CreateDeliveryAsync(deliveryScene2, 4m);
        var pickupScene2 = await SellAsync(branch2, session2.Id, variant2, qty: 4m, pickupRequiredQuantity: 4m);
        await CreatePickupAsync(pickupScene2, 4m);

        // Switch back to tenant 1: its own rows must still be visible, but tenant 2's must never appear.
        Authorize(owner1.AccessToken);

        var deliveryReport = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        deliveryReport!.Page.Items.Should().ContainSingle(r => r.SaleId == deliveryScene1.SaleId);
        deliveryReport.Page.Items.Should().NotContain(r => r.SaleId == deliveryScene2.SaleId);

        var pickupReport = await Client.GetFromJsonAsync<PickupReportResultDto>("/api/reports/pickup", TestJson.Options);
        pickupReport!.Page.Items.Should().ContainSingle(r => r.SaleId == pickupScene1.SaleId);
        pickupReport.Page.Items.Should().NotContain(r => r.SaleId == pickupScene2.SaleId);
    }
}
