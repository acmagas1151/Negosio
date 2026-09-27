using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Reports;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Reports;

public class ReportsTests : IntegrationTest
{
    public ReportsTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<(Guid BranchId, Guid RegisterId, Guid SessionId, Guid VariantId, decimal Price)> ArrangeAsync(
        decimal price = 100m, decimal stock = 50m)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: stock);
        return (branchId, register.Id, session.Id, variantId, price);
    }

    private Task<SaleResultDto> SellAsync(Guid branchId, Guid sessionId, Guid variantId, decimal qty, decimal cashReceived) =>
        CheckoutOkAsync(new CheckoutRequest(
            branchId, sessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) }));

    // ---- Authorization & branch scoping ----

    [Fact]
    public async Task Cashier_cannot_reach_reports_at_all()
    {
        var scene = await ArrangeAsync();
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, scene.BranchId);
        Authorize(cashierToken);

        var res = await Client.GetAsync("/api/reports/overview?period=Today");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_sees_tenant_wide_totals_manager_is_forced_to_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var mainRegister = await CreateRegisterAsync(mainId, "M1", "M1");
        var mainSession = await OpenSessionAsync(mainRegister.Id);
        var category = await CreateCategoryAsync();
        var (_, mainVariantId) = await SeedStockedProductAsync(mainId, category.Id, sku: "MAIN-SKU", sellingPrice: 100m, openingStock: 20m);
        await SellAsync(mainId, mainSession.Id, mainVariantId, 1m, 200m);

        // A user may only have one open session at a time (CashierSessionOpen) — close the first
        // before the same Owner opens a second one on a different branch.
        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/close", new CloseRegisterSessionRequest(1200m)))
            .EnsureSuccessStatusCode();

        var bgcRegister = await CreateRegisterAsync(bgc.Id, "B1", "B1");
        var bgcSession = await OpenSessionAsync(bgcRegister.Id);
        var (_, bgcVariantId) = await SeedStockedProductAsync(bgc.Id, category.Id, sku: "BGC-SKU", sellingPrice: 150m, openingStock: 20m);
        await SellAsync(bgc.Id, bgcSession.Id, bgcVariantId, 1m, 200m);

        // Owner, no branch filter: sees both.
        var ownerOverview = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Today", TestJson.Options);
        ownerOverview!.Kpis.NetSales.Should().Be(250m); // 100 + 150
        ownerOverview.Kpis.CompletedTransactions.Should().Be(2);

        // Owner, explicit branch filter: sees only that branch.
        var ownerBgcOnly = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            $"/api/reports/overview?period=Today&branchId={bgc.Id}", TestJson.Options);
        ownerBgcOnly!.Kpis.NetSales.Should().Be(150m);

        // Manager assigned to Main: forced to Main regardless of what they ask for.
        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainId);
        Authorize(managerToken);

        var managerDefault = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Today", TestJson.Options);
        managerDefault!.Kpis.NetSales.Should().Be(100m);

        // Even explicitly requesting the other branch's id must not leak BGC's data to the Main manager.
        var managerAttemptBgc = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            $"/api/reports/overview?period=Today&branchId={bgc.Id}", TestJson.Options);
        managerAttemptBgc!.Kpis.NetSales.Should().Be(100m, "a branch-scoped Manager must always be forced to their own branch");
    }

    // ---- Data correctness ----

    [Fact]
    public async Task Voided_sale_is_excluded_from_totals_and_counted_separately()
    {
        var scene = await ArrangeAsync(price: 100m);
        var sale = await SellAsync(scene.BranchId, scene.SessionId, scene.VariantId, 1m, 100m);

        var voidRes = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/void", new VoidSaleRequest("Test void"));
        voidRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var overview = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Today", TestJson.Options);

        overview!.Kpis.NetSales.Should().Be(0m, "the only sale in range was voided");
        overview.Kpis.GrossSales.Should().Be(0m);
        overview.Kpis.CompletedTransactions.Should().Be(0);
        overview.Kpis.VoidedSalesCount.Should().Be(1);
        overview.Kpis.VoidedSalesValue.Should().Be(100m);
    }

    [Fact]
    public async Task Fully_refunded_sale_stays_in_gross_and_net_and_is_offset_by_returns()
    {
        var scene = await ArrangeAsync(price: 100m);
        var sale = await SellAsync(scene.BranchId, scene.SessionId, scene.VariantId, 1m, 100m);
        var detail = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);
        var saleItemId = detail!.Items.Single().Id;

        var returnRes = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(saleItemId, 1m) }, "Full return", PaymentMethod.Cash, null));
        returnRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var overview = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Today", TestJson.Options);

        overview!.Kpis.GrossSales.Should().Be(100m, "the sale stays in history at its original amount");
        overview.Kpis.NetSales.Should().Be(100m);
        overview.Kpis.Returns.Should().Be(100m);
        overview.Kpis.NetCollected.Should().Be(0m, "net sales minus the return nets to zero");
        overview.Kpis.ReturnsCount.Should().Be(1);
    }

    [Fact]
    public async Task Return_is_attributed_to_its_own_date_not_the_original_sales_date()
    {
        var scene = await ArrangeAsync(price: 100m);
        var sale = await SellAsync(scene.BranchId, scene.SessionId, scene.VariantId, 1m, 100m);
        var detail = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);
        var saleItemId = detail!.Items.Single().Id;

        // Push the ORIGINAL sale back to yesterday — the return itself is made "now" (today).
        await BackdateSaleCreatedAtAsync(sale.SaleId, DateTime.UtcNow.AddDays(-1));

        var returnRes = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(saleItemId, 1m) }, "Late return", PaymentMethod.Cash, null));
        returnRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var today = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Today", TestJson.Options);
        today!.Kpis.GrossSales.Should().Be(0m, "the sale itself happened yesterday, not today");
        today.Kpis.Returns.Should().Be(100m, "but the return happened today, so it counts today");

        var yesterday = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Yesterday", TestJson.Options);
        yesterday!.Kpis.GrossSales.Should().Be(100m, "the sale shows up on the day it actually happened");
        yesterday.Kpis.Returns.Should().Be(0m, "the return did not happen yesterday");
    }

    [Fact]
    public async Task Split_tender_sale_contributes_to_every_payment_method_it_used()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[]
            {
                new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 40m),
                new CheckoutPaymentInput(PaymentMethod.GCash, Amount: 60m),
            }));
        sale.GrandTotal.Should().Be(100m);

        var overview = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Today", TestJson.Options);

        overview!.PaymentMethods.Should().HaveCount(2);
        overview.PaymentMethods.Single(p => p.Method == PaymentMethod.Cash).Amount.Should().Be(40m);
        overview.PaymentMethods.Single(p => p.Method == PaymentMethod.GCash).Amount.Should().Be(60m);
        overview.PaymentMethods.Sum(p => p.PaymentCount).Should().Be(2, "two payment records from one sale, counted as payments not transactions");
    }

    [Fact]
    public async Task Filters_combine_branch_register_and_cashier_together()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var registerA = await CreateRegisterAsync(branchId, "RA", "RA");
        var registerB = await CreateRegisterAsync(branchId, "RB", "RB");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        // Owner sells on register A.
        var sessionA = await OpenSessionAsync(registerA.Id);
        await SellAsync(branchId, sessionA.Id, variantId, 1m, 100m);

        // Cashier sells on register B.
        Authorize(cashierToken);
        var sessionB = await OpenSessionAsync(registerB.Id);
        await SellAsync(branchId, sessionB.Id, variantId, 1m, 100m);

        Authorize(owner.AccessToken);
        var filtered = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            $"/api/reports/overview?period=Today&registerId={registerB.Id}&cashierId={cashierId}", TestJson.Options);

        filtered!.Kpis.NetSales.Should().Be(100m, "only the cashier's sale on register B should match both filters together");
        filtered.Kpis.CompletedTransactions.Should().Be(1);
    }

    // ---- Top products / categories ----

    [Fact]
    public async Task Top_products_and_category_performance_rank_by_sales_amount()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var drinks = await CreateCategoryAsync("Drinks");
        var snacks = await CreateCategoryAsync("Snacks");

        var (_, colaId) = await SeedStockedProductAsync(branchId, drinks.Id, name: "Cola", sku: "COLA-1", sellingPrice: 50m, openingStock: 50m);
        var (_, chipsId) = await SeedStockedProductAsync(branchId, snacks.Id, name: "Chips", sku: "CHIPS-1", sellingPrice: 30m, openingStock: 50m);

        await SellAsync(branchId, session.Id, colaId, 5m, 250m);  // 250 sales amount
        await SellAsync(branchId, session.Id, chipsId, 2m, 60m);  // 60 sales amount

        var topProducts = await Client.GetFromJsonAsync<List<TopProductDto>>(
            "/api/reports/top-products?period=Today&top=10", TestJson.Options);
        topProducts!.Should().HaveCount(2);
        topProducts[0].ProductName.Should().Be("Cola");
        topProducts[0].SalesAmount.Should().Be(250m);
        topProducts[0].Sku.Should().Be("COLA-1");
        topProducts[0].CategoryName.Should().Be("Drinks");

        var categories = await Client.GetFromJsonAsync<List<CategoryPerformanceDto>>(
            "/api/reports/categories?period=Today", TestJson.Options);
        categories!.Should().HaveCount(2);
        var drinksRow = categories.Single(c => c.CategoryName == "Drinks");
        drinksRow.SalesAmount.Should().Be(250m);
        drinksRow.PercentageOfSales.Should().Be(80.6m); // 250 / 310 * 100, rounded to 1dp
    }

    [Fact]
    public async Task Trend_hourly_and_daily_buckets_translate_and_sum_correctly()
    {
        var scene = await ArrangeAsync(price: 100m);
        await SellAsync(scene.BranchId, scene.SessionId, scene.VariantId, 1m, 100m);

        var today = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Today", TestJson.Options);
        today!.TrendIsHourly.Should().BeTrue();
        today.Trend.Should().HaveCount(24);
        today.Trend.Sum(t => t.NetSales).Should().Be(100m);
        today.Trend.Sum(t => t.Transactions).Should().Be(1);

        var last7 = await Client.GetFromJsonAsync<ReportsOverviewDto>(
            "/api/reports/overview?period=Last7Days", TestJson.Options);
        last7!.TrendIsHourly.Should().BeFalse();
        last7.Trend.Should().HaveCount(7);
        last7.Trend.Sum(t => t.NetSales).Should().Be(100m);
    }

    // ---- Delivery report ----

    [Fact]
    public async Task Delivery_report_totals_charge_a_multi_schedule_sale_exactly_once()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1160m) },
            Method: FulfillmentMethod.Delivery,
            DeliveryCharge: 60m));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

        // A sale now has at most one ACTIVE schedule at a time, so "two schedule rows on one sale" means
        // the first was cancelled and a second created afterward — still two historical DeliveryReceipt
        // rows for the report to total, exactly as a genuine reschedule would produce. Cancelling with
        // DeliverLater atomically creates its own replacement schedule (Task 3B), so the second row comes
        // from the cancel itself, not a separate manual create.
        var firstResp = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null));
        var first = (await firstResp.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
        var cancelResp = await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel",
            new CancelDeliveryRequest("Reschedule", CancellationDisposition.DeliverLater, null,
                new DeliveryReplacementInput(today, "Juan", "123 Ayala Ave", null, null)));
        cancelResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        report!.Page.Items.Should().HaveCount(2); // two schedule rows
        report.Totals.TotalSchedules.Should().Be(2);
        report.Totals.DistinctSalesCount.Should().Be(1);
        report.Totals.TotalDeliveryCharges.Should().Be(60m); // NOT 120m — charged once per sale, not per schedule
        report.Totals.AverageDeliveryChargePerSale.Should().Be(60m);
    }

    [Fact]
    public async Task Delivery_report_filters_by_status()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1100m) },
            Method: FulfillmentMethod.Delivery));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var drResp = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null));
        var dr = (await drResp.Content.ReadFromJsonAsync<FulfillmentScheduleDto>(TestJson.Options))!;
        await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);

        var deliveredOnly = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            $"/api/reports/deliveries?Status={(int)FulfillmentStatus.Completed}", TestJson.Options);
        deliveredOnly!.Page.Items.Should().ContainSingle(r => r.DeliveryReceiptId == dr.Id);

        var pendingOnly = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            $"/api/reports/deliveries?Status={(int)FulfillmentStatus.Pending}", TestJson.Options);
        pendingOnly!.Page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Delivery_fulfillment_report_flags_a_sale_that_still_needs_scheduling()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        // Checkout with Method = Delivery, but do NOT create a schedule afterward — checkout succeeded
        // but the follow-up schedule-creation call never happened.
        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) },
            Method: FulfillmentMethod.Delivery));

        var report = await Client.GetFromJsonAsync<DeliveryFulfillmentReportResultDto>(
            "/api/reports/delivery-fulfillment", TestJson.Options);

        var row = report!.Page.Items.Should().ContainSingle(r => r.SaleId == sale.SaleId).Subject;
        row.NeedsScheduling.Should().BeTrue("checkout succeeded but no schedule was ever created for it");
        row.ScheduleCount.Should().Be(0);
    }

    [Fact]
    public async Task Delivery_fulfillment_report_does_not_flag_a_sale_with_an_active_schedule()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) },
            Method: FulfillmentMethod.Delivery));

        // Same as above, but DO create a delivery schedule for the sale afterward.
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null));

        var report = await Client.GetFromJsonAsync<DeliveryFulfillmentReportResultDto>(
            "/api/reports/delivery-fulfillment", TestJson.Options);

        var row = report!.Page.Items.Should().ContainSingle(r => r.SaleId == sale.SaleId).Subject;
        row.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.PendingDelivery);
        row.NeedsScheduling.Should().BeFalse();
        row.ScheduleCount.Should().Be(1);
    }

    // ---- Branch performance ----

    [Fact]
    public async Task GetBranchPerformanceAsync_GroupsByBranchAndExcludesVoided()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var mainRegister = await CreateRegisterAsync(mainId, "M1", "M1");
        var mainSession = await OpenSessionAsync(mainRegister.Id);
        var category = await CreateCategoryAsync();
        var (_, mainVariantId) = await SeedStockedProductAsync(
            mainId, category.Id, sku: "MAIN-SKU", sellingPrice: 500m, openingStock: 20m);

        // Branch A: one completed sale (500) plus a second sale that gets voided (100) — the void
        // must land in its own bucket and never inflate/deflate NetSales.
        var saleA = await SellAsync(mainId, mainSession.Id, mainVariantId, 1m, 500m);
        var (_, voidVariantId) = await SeedStockedProductAsync(
            mainId, category.Id, name: "Void Candidate", sku: "MAIN-VOID-SKU", sellingPrice: 100m, openingStock: 20m);
        var saleToVoid = await SellAsync(mainId, mainSession.Id, voidVariantId, 1m, 100m);
        var voidRes = await Client.PostAsJsonAsync($"/api/sales/{saleToVoid.SaleId}/void", new VoidSaleRequest("Test void"));
        voidRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // A user may only have one open session at a time — close before opening a second on branch B.
        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/close", new CloseRegisterSessionRequest(1200m)))
            .EnsureSuccessStatusCode();

        // Branch B: one completed sale (300), no voids.
        var bgcRegister = await CreateRegisterAsync(bgc.Id, "B1", "B1");
        var bgcSession = await OpenSessionAsync(bgcRegister.Id);
        var (_, bgcVariantId) = await SeedStockedProductAsync(
            bgc.Id, category.Id, sku: "BGC-SKU", sellingPrice: 300m, openingStock: 20m);
        var saleB = await SellAsync(bgc.Id, bgcSession.Id, bgcVariantId, 1m, 300m);
        saleB.GrandTotal.Should().Be(300m);

        var result = await Client.GetFromJsonAsync<BranchPerformanceResultDto>(
            "/api/reports/branch-performance?period=Last30Days", TestJson.Options);

        result!.Rows.Should().HaveCount(2);
        var rowA = result.Rows.Single(r => r.BranchId == mainId);
        rowA.NetSales.Should().Be(500m, "the voided 100 must not be included");
        rowA.GrossSales.Should().Be(500m);
        rowA.CompletedTransactions.Should().Be(1);
        rowA.VoidedSalesCount.Should().Be(1);
        rowA.VoidedSalesValue.Should().Be(100m);

        var rowB = result.Rows.Single(r => r.BranchId == bgc.Id);
        rowB.NetSales.Should().Be(300m);
        rowB.CompletedTransactions.Should().Be(1);
        rowB.VoidedSalesCount.Should().Be(0);
        rowB.VoidedSalesValue.Should().Be(0m);
    }

    [Fact]
    public async Task Branch_performance_manager_is_forced_to_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var mainRegister = await CreateRegisterAsync(mainId, "M1", "M1");
        var mainSession = await OpenSessionAsync(mainRegister.Id);
        var category = await CreateCategoryAsync();
        var (_, mainVariantId) = await SeedStockedProductAsync(mainId, category.Id, sku: "MAIN-SKU", sellingPrice: 100m, openingStock: 20m);
        await SellAsync(mainId, mainSession.Id, mainVariantId, 1m, 100m);

        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/close", new CloseRegisterSessionRequest(1200m)))
            .EnsureSuccessStatusCode();

        var bgcRegister = await CreateRegisterAsync(bgc.Id, "B1", "B1");
        var bgcSession = await OpenSessionAsync(bgcRegister.Id);
        var (_, bgcVariantId) = await SeedStockedProductAsync(bgc.Id, category.Id, sku: "BGC-SKU", sellingPrice: 150m, openingStock: 20m);
        await SellAsync(bgc.Id, bgcSession.Id, bgcVariantId, 1m, 150m);

        var managerToken = await AddTenantUserTokenAsync("branchperf-mgr@example.com", UserRole.Manager, mainId);
        Authorize(managerToken);

        var managerResult = await Client.GetFromJsonAsync<BranchPerformanceResultDto>(
            "/api/reports/branch-performance?period=Last30Days", TestJson.Options);
        managerResult!.Rows.Should().ContainSingle().Which.BranchId.Should().Be(mainId);

        // Even explicitly requesting the other branch's id must not leak BGC's data to the Main manager.
        var managerAttemptBgc = await Client.GetFromJsonAsync<BranchPerformanceResultDto>(
            $"/api/reports/branch-performance?period=Last30Days&branchId={bgc.Id}", TestJson.Options);
        managerAttemptBgc!.Rows.Should().ContainSingle().Which.BranchId.Should().Be(mainId,
            "a branch-scoped Manager must always be forced to their own branch");
    }

    [Fact]
    public async Task A_sale_with_nothing_marked_for_delivery_never_appears_in_the_fulfillment_report()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null) }, // nothing for delivery (Method defaults to TakeNow)
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) }));

        var report = await Client.GetFromJsonAsync<DeliveryFulfillmentReportResultDto>(
            "/api/reports/delivery-fulfillment", TestJson.Options);

        report!.Page.Items.Should().NotContain(r => r.SaleId == sale.SaleId);
    }

    // ---- Register performance ----

    [Fact]
    public async Task GetRegisterPerformanceAsync_GroupsByRegisterWithSplitTenderSafePaymentBreakdown()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 300m, openingStock: 10m);

        // Split-tender sale: 200 Cash + 100 GCash = 300 total — must contribute once per method to the
        // payment breakdown, but NetSales stays at the single sale's GrandTotal (never double-counted).
        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[]
            {
                new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m),
                new CheckoutPaymentInput(PaymentMethod.GCash, Amount: 100m),
            }));
        sale.GrandTotal.Should().Be(300m);

        var cashInRes = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 50m, "Float top-up"));
        cashInRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var cashOutRes = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashOut, 20m, "Petty cash"));
        cashOutRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await Client.GetFromJsonAsync<RegisterPerformanceResultDto>(
            "/api/reports/register-performance?period=Last30Days", TestJson.Options);

        var row = Assert.Single(result!.Rows);
        row.RegisterId.Should().Be(register.Id);
        row.BranchId.Should().Be(branchId);
        row.NetSales.Should().Be(300m);
        row.CompletedTransactions.Should().Be(1);
        row.PaymentMethods.Should().HaveCount(2, "Cash and GCash each contribute their own row");
        row.PaymentMethods.Single(p => p.Method == PaymentMethod.Cash).Amount.Should().Be(200m);
        row.PaymentMethods.Single(p => p.Method == PaymentMethod.GCash).Amount.Should().Be(100m);
        row.PaymentMethods.Single(p => p.Method == PaymentMethod.Cash).Percentage.Should().Be(66.7m);
        row.PaymentMethods.Single(p => p.Method == PaymentMethod.GCash).Percentage.Should().Be(33.3m);
        row.CashIn.Should().Be(50m);
        row.CashOut.Should().Be(20m);
    }

    [Fact]
    public async Task Register_performance_manager_is_forced_to_their_own_branch_and_cash_movements_stay_scoped()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var mainRegister = await CreateRegisterAsync(mainId, "M1", "M1");
        var mainSession = await OpenSessionAsync(mainRegister.Id);
        var category = await CreateCategoryAsync();
        var (_, mainVariantId) = await SeedStockedProductAsync(mainId, category.Id, sku: "MAIN-SKU", sellingPrice: 100m, openingStock: 20m);
        await SellAsync(mainId, mainSession.Id, mainVariantId, 1m, 100m);
        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 40m, "Main float"))).EnsureSuccessStatusCode();

        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/close", new CloseRegisterSessionRequest(1200m)))
            .EnsureSuccessStatusCode();

        var bgcRegister = await CreateRegisterAsync(bgc.Id, "B1", "B1");
        var bgcSession = await OpenSessionAsync(bgcRegister.Id);
        var (_, bgcVariantId) = await SeedStockedProductAsync(bgc.Id, category.Id, sku: "BGC-SKU", sellingPrice: 150m, openingStock: 20m);
        await SellAsync(bgc.Id, bgcSession.Id, bgcVariantId, 1m, 150m);
        (await Client.PostAsJsonAsync($"/api/register-sessions/{bgcSession.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 70m, "BGC float"))).EnsureSuccessStatusCode();

        var managerToken = await AddTenantUserTokenAsync("regperf-mgr@example.com", UserRole.Manager, mainId);
        Authorize(managerToken);

        var managerResult = await Client.GetFromJsonAsync<RegisterPerformanceResultDto>(
            "/api/reports/register-performance?period=Last30Days", TestJson.Options);
        var managerRow = managerResult!.Rows.Should().ContainSingle().Subject;
        managerRow.RegisterId.Should().Be(mainRegister.Id);
        managerRow.CashIn.Should().Be(40m, "the manager must never see BGC's cash movements");

        // Even explicitly requesting the other branch's id must not leak BGC's data to the Main manager.
        var managerAttemptBgc = await Client.GetFromJsonAsync<RegisterPerformanceResultDto>(
            $"/api/reports/register-performance?period=Last30Days&branchId={bgc.Id}", TestJson.Options);
        managerAttemptBgc!.Rows.Should().ContainSingle().Which.RegisterId.Should().Be(mainRegister.Id,
            "a branch-scoped Manager must always be forced to their own branch");
    }

    // ---- Register session reconciliation (session-granular, closed sessions only) ----

    [Fact]
    public async Task GetRegisterSessionReconciliation_ReturnsOnlyClosedSessionsWithPersistedFigures()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id, openingCash: 1000m);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 200m, openingStock: 10m);

        // Exact-cash sale so ExpectedCash comes out to a clean 1200 (1000 opening + 200 cash sales) —
        // ClosingCash matches it exactly, so CashDifference should be 0.
        await SellAsync(branchId, session.Id, variantId, 1m, 200m);
        var closeRes = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/close",
            new CloseRegisterSessionRequest(1200m));
        closeRes.EnsureSuccessStatusCode();

        // A second, still-OPEN session on another register must never appear in this report — it has
        // no reconciliation figures yet (they're only set at Close).
        var openRegister = await CreateRegisterAsync(branchId, "Second Counter", "R2");
        await OpenSessionAsync(openRegister.Id, openingCash: 500m);

        var result = await Client.GetFromJsonAsync<PagedResult<RegisterSessionReconciliationRowDto>>(
            "/api/reports/register-sessions?period=Last30Days", TestJson.Options);

        var row = Assert.Single(result!.Items);
        row.SessionId.Should().Be(session.Id);
        row.RegisterId.Should().Be(register.Id);
        row.BranchId.Should().Be(branchId);
        row.OpeningCash.Should().Be(1000m);
        row.ClosingCash.Should().Be(1200m);
        row.ExpectedCash.Should().Be(1200m);
        row.CashDifference.Should().Be(0m);
        row.GrossCashSales.Should().Be(200m);
        row.VoidedCashSales.Should().Be(0m);
        row.RefundCashOut.Should().Be(0m);
        row.CashIn.Should().Be(0m);
        row.CashOut.Should().Be(0m);
    }

    [Fact]
    public async Task RegisterSessionReconciliation_ManagerIsForcedToTheirOwnBranch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var mainRegister = await CreateRegisterAsync(mainId, "M1", "M1");
        var mainSession = await OpenSessionAsync(mainRegister.Id, openingCash: 1000m);
        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession.Id}/close",
            new CloseRegisterSessionRequest(1000m))).EnsureSuccessStatusCode();

        var bgcRegister = await CreateRegisterAsync(bgc.Id, "B1", "B1");
        var bgcSession = await OpenSessionAsync(bgcRegister.Id, openingCash: 500m);
        (await Client.PostAsJsonAsync($"/api/register-sessions/{bgcSession.Id}/close",
            new CloseRegisterSessionRequest(500m))).EnsureSuccessStatusCode();

        var managerToken = await AddTenantUserTokenAsync("regrecon-mgr@example.com", UserRole.Manager, mainId);
        Authorize(managerToken);

        var managerResult = await Client.GetFromJsonAsync<PagedResult<RegisterSessionReconciliationRowDto>>(
            "/api/reports/register-sessions?period=Last30Days", TestJson.Options);
        managerResult!.Items.Should().ContainSingle().Which.SessionId.Should().Be(mainSession.Id);

        // Even explicitly requesting the other branch's id must not leak BGC's session to the Main manager.
        var managerAttemptBgc = await Client.GetFromJsonAsync<PagedResult<RegisterSessionReconciliationRowDto>>(
            $"/api/reports/register-sessions?period=Last30Days&branchId={bgc.Id}", TestJson.Options);
        managerAttemptBgc!.Items.Should().ContainSingle().Which.SessionId.Should().Be(mainSession.Id,
            "a branch-scoped Manager must always be forced to their own branch");
    }

    // ---- Cashier performance ----

    [Fact]
    public async Task GetCashierPerformanceAsync_AttributesByCheckoutUserRegardlessOfRole()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var registerA = await CreateRegisterAsync(branchId, "RA", "RA");
        var registerB = await CreateRegisterAsync(branchId, "RB", "RB");
        var category = await CreateCategoryAsync();
        var (_, cashierVariantId) = await SeedStockedProductAsync(
            branchId, category.Id, name: "Cashier Item", sku: "CASH-SKU", sellingPrice: 400m, openingStock: 10m);
        var (_, managerVariantId) = await SeedStockedProductAsync(
            branchId, category.Id, name: "Manager Item", sku: "MGR-SKU", sellingPrice: 250m, openingStock: 10m);

        var cashierToken = await AddTenantUserTokenAsync("cashierperf@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);
        var managerToken = await AddTenantUserTokenAsync("managerperf@example.com", UserRole.Manager, branchId);
        var managerId = await GetUserIdFromTokenAsync(managerToken);

        Authorize(cashierToken);
        var cashierSession = await OpenSessionAsync(registerA.Id);
        await SellAsync(branchId, cashierSession.Id, cashierVariantId, 1m, 400m);

        // A Manager ringing up their own sale — this must show up too, since this report attributes by
        // whoever actually completed the checkout, never filtered to Role == Cashier.
        Authorize(managerToken);
        var managerSession = await OpenSessionAsync(registerB.Id);
        await SellAsync(branchId, managerSession.Id, managerVariantId, 1m, 250m);

        Authorize(owner.AccessToken);
        var result = await Client.GetFromJsonAsync<CashierPerformanceResultDto>(
            "/api/reports/cashier-performance?period=Last30Days", TestJson.Options);

        result!.Rows.Should().HaveCount(2, "both the Cashier AND the Manager rang up their own sale");
        result.Rows.Single(r => r.CashierUserId == cashierId).NetSales.Should().Be(400m);
        result.Rows.Single(r => r.CashierUserId == managerId).NetSales.Should().Be(250m);
    }

    [Fact]
    public async Task GetCashierPerformanceAsync_VoidActivityIsAttributedToTheVoidingActorNotTheOriginalCashier()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var category = await CreateCategoryAsync();
        var (_, keepVariantId) = await SeedStockedProductAsync(
            branchId, category.Id, name: "Kept Item", sku: "KEEP-SKU", sellingPrice: 200m, openingStock: 10m);
        var (_, voidVariantId) = await SeedStockedProductAsync(
            branchId, category.Id, name: "Void Item", sku: "VOID-SKU", sellingPrice: 100m, openingStock: 10m);

        var cashierToken = await AddTenantUserTokenAsync("voidcashier@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);
        var managerToken = await AddTenantUserTokenAsync("voidmanager@example.com", UserRole.Manager, branchId);
        var managerId = await GetUserIdFromTokenAsync(managerToken);

        Authorize(cashierToken);
        var session = await OpenSessionAsync(register.Id);
        await SellAsync(branchId, session.Id, keepVariantId, 1m, 200m);
        var saleToVoid = await SellAsync(branchId, session.Id, voidVariantId, 1m, 100m);

        // Manager B voids Cashier A's sale — the void must be attributed to the VOIDING actor
        // (Sale.VoidedByUserId), never the original checkout cashier (Sale.CreatedByUserId).
        Authorize(managerToken);
        var voidRes = await Client.PostAsJsonAsync($"/api/sales/{saleToVoid.SaleId}/void", new VoidSaleRequest("Test void"));
        voidRes.StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(owner.AccessToken);
        var result = await Client.GetFromJsonAsync<CashierPerformanceResultDto>(
            "/api/reports/cashier-performance?period=Last30Days", TestJson.Options);

        var cashierRow = result!.Rows.Single(r => r.CashierUserId == cashierId);
        cashierRow.NetSales.Should().Be(200m, "the voided 100 sale must not count toward the original cashier's Net sales");
        cashierRow.GrossSales.Should().Be(200m);
        cashierRow.CompletedTransactions.Should().Be(1);
        cashierRow.VoidedSalesCount.Should().Be(0, "the void was performed by the Manager, not this Cashier");
        cashierRow.VoidedSalesValue.Should().Be(0m);

        var managerRow = result.Rows.Single(r => r.CashierUserId == managerId);
        managerRow.VoidedSalesCount.Should().Be(1, "attributed to whoever performed the void");
        managerRow.VoidedSalesValue.Should().Be(100m);
        managerRow.NetSales.Should().Be(0m, "the Manager did not ring up any sale themselves");
        managerRow.CompletedTransactions.Should().Be(0);
    }

    [Fact]
    public async Task GetCashierPerformanceAsync_VoidApprovalIsAttributedToTheApprovingManagerNotTheVoidingCashier()
    {
        // Actor (who voided) and approver (who authorized it) must not be conflated into one field —
        // a Cashier with no void grant voids their own sale under a Manager's approval: the Cashier's row
        // must show the void itself (VoidedSalesCount), while the Manager's row must show the APPROVAL
        // (VoidApprovalsCount) — distinctly, even though the Manager never personally voided anything.
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(
            branchId, category.Id, name: "Approval Item", sku: "APPROVAL-SKU", sellingPrice: 150m, openingStock: 10m);

        var cashierToken = await AddTenantUserTokenAsync("approvalcashier@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);
        var managerId = await CreateManagerAsync("approvalmanager@example.com", "Manager123!", branchId);

        Authorize(cashierToken);
        var session = await OpenSessionAsync(register.Id);
        var sale = await SellAsync(branchId, session.Id, variantId, 1m, 150m);

        // This Cashier has no void grant, so the void requires a Manager's approval credentials.
        var voidRes = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/void",
            new VoidSaleRequest("Test void", new VoidSaleApprovalInput("approvalmanager@example.com", "Manager123!")));
        voidRes.StatusCode.Should().Be(HttpStatusCode.OK);

        Authorize(owner.AccessToken);
        var result = await Client.GetFromJsonAsync<CashierPerformanceResultDto>(
            "/api/reports/cashier-performance?period=Last30Days", TestJson.Options);

        var cashierRow = result!.Rows.Single(r => r.CashierUserId == cashierId);
        cashierRow.VoidedSalesCount.Should().Be(1, "the Cashier is the ACTOR who voided their own sale");
        cashierRow.VoidApprovalsCount.Should().Be(0, "the Cashier was not the approver of their own void");

        var managerRow = result.Rows.Single(r => r.CashierUserId == managerId);
        managerRow.VoidApprovalsCount.Should().Be(1, "the Manager approved the void — a distinct bucket from who performed it");
        managerRow.VoidedSalesCount.Should().Be(0, "the Manager did not perform the void themselves, only approved it");
        managerRow.NetSales.Should().Be(0m, "the Manager never rang up a sale — only shows up here as an approver");
    }

    [Fact]
    public async Task GetCashierPerformanceAsync_ReturnActivityIsAttributedToWhoeverProcessedIt()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 10m);

        var cashierToken = await AddTenantUserTokenAsync("returncashier@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);
        var managerToken = await AddTenantUserTokenAsync("returnmanager@example.com", UserRole.Manager, branchId);
        var managerId = await GetUserIdFromTokenAsync(managerToken);

        Authorize(cashierToken);
        var session = await OpenSessionAsync(register.Id);
        var sale = await SellAsync(branchId, session.Id, variantId, 1m, 100m);
        var detail = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);
        var saleItemId = detail!.Items.Single().Id;

        // Manager processes the return for Cashier A's sale — return activity must be attributed to
        // whoever processed it (SaleReturn.CreatedByUserId), never the original checkout cashier.
        Authorize(managerToken);
        var returnRes = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/returns",
            new CreateReturnRequest(new[] { new ReturnLineInput(saleItemId, 1m) }, "Test return", PaymentMethod.Cash, null));
        returnRes.StatusCode.Should().Be(HttpStatusCode.Created);

        Authorize(owner.AccessToken);
        var result = await Client.GetFromJsonAsync<CashierPerformanceResultDto>(
            "/api/reports/cashier-performance?period=Last30Days", TestJson.Options);

        var cashierRow = result!.Rows.Single(r => r.CashierUserId == cashierId);
        cashierRow.NetSales.Should().Be(100m, "the sale itself stays at its original amount; the return is a separate bucket");
        cashierRow.ReturnsCount.Should().Be(0, "the return was processed by the Manager, not this Cashier");
        cashierRow.ReturnsValue.Should().Be(0m);

        var managerRow = result.Rows.Single(r => r.CashierUserId == managerId);
        managerRow.ReturnsCount.Should().Be(1);
        managerRow.ReturnsValue.Should().Be(100m);
    }

    [Fact]
    public async Task GetCashierPerformanceAsync_HistoricalSaleKeepsItsOriginalBranchRegardlessOfLaterReassignment()
    {
        // A User has at most one BranchId — there is no multi-branch assignment. What must hold is that
        // a Sale's own BranchId (fixed at checkout) is what this report's branch scoping honors, not the
        // cashier's CURRENT branch — verified by reassigning the cashier's branch after checkout.
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var cashierToken = await AddTenantUserTokenAsync("branchmove-cashier@example.com", UserRole.Cashier, mainId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        var register = await CreateRegisterAsync(mainId, "M1", "M1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(mainId, category.Id, sellingPrice: 400m, openingStock: 10m);

        Authorize(cashierToken);
        var session = await OpenSessionAsync(register.Id);
        await SellAsync(mainId, session.Id, variantId, 1m, 400m);
        (await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/close", new CloseRegisterSessionRequest(1400m)))
            .EnsureSuccessStatusCode();

        // The cashier moves to Branch BGC after checkout.
        Authorize(owner.AccessToken);
        (await Client.PostAsJsonAsync($"/api/staff/{cashierId}/branch", new ChangeStaffBranchRequest(bgc.Id.ToString())))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resultForMain = await Client.GetFromJsonAsync<CashierPerformanceResultDto>(
            $"/api/reports/cashier-performance?period=Last30Days&branchId={mainId}", TestJson.Options);
        resultForMain!.Rows.Should().ContainSingle(r => r.CashierUserId == cashierId && r.NetSales == 400m);

        var resultForBgc = await Client.GetFromJsonAsync<CashierPerformanceResultDto>(
            $"/api/reports/cashier-performance?period=Last30Days&branchId={bgc.Id}", TestJson.Options);
        resultForBgc!.Rows.Should().NotContain(r => r.CashierUserId == cashierId,
            "the sale happened at Main, not BGC, regardless of where the cashier is assigned now");
    }
}
