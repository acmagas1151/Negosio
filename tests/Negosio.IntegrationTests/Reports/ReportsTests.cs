using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Reports;
using Negosio.Application.Sales;
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
    public async Task Delivery_fulfillment_report_shows_one_row_per_sale_with_aggregated_quantities()
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

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null));

        var report = await Client.GetFromJsonAsync<DeliveryFulfillmentReportResultDto>(
            "/api/reports/delivery-fulfillment", TestJson.Options);

        var row = report!.Page.Items.Should().ContainSingle(r => r.SaleId == sale.SaleId).Subject;
        row.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.PendingDelivery);
        row.TotalDeliveryRequiredQuantity.Should().Be(10m); // whole-sale intent, per Task 1
        row.TotalPendingQuantity.Should().Be(10m);
        row.TotalUnscheduledQuantity.Should().Be(0m); // a create now schedules 100% in one shot
        row.ScheduleCount.Should().Be(1);
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
}
