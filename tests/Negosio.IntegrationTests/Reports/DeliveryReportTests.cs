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
using Xunit;

namespace Negosio.IntegrationTests.Reports;

public class DeliveryReportTests : IntegrationTest
{
    public DeliveryReportTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private static CreateDeliveryReceiptRequest Req(string recipientName = "Juan Dela Cruz") =>
        new(recipientName, "123 Ayala Ave, Makati", "0917 111 2222", "Leave at guardhouse", null);

    private async Task<(Guid BranchId, Guid SessionId, Guid VariantId)> ArrangeAsync(
        Guid branchId, decimal price = 100m, string categoryName = "Beverages")
    {
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        var register = await CreateRegisterAsync(branchId, $"Counter {unique}", unique);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync(categoryName);
        var (_, variantId) = await SeedStockedProductAsync(
            branchId, category.Id, sku: $"SKU-{unique}", sellingPrice: price, openingStock: 20m);
        return (branchId, session.Id, variantId);
    }

    private async Task<Guid> DeliverySaleAsync(
        Guid branchId, Guid sessionId, Guid variantId, decimal price, decimal deliveryCharge, string recipientName = "Juan Dela Cruz")
    {
        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, sessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: price + deliveryCharge + 100m) },
            DeliveryCharge: deliveryCharge));
        (await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipt", Req(recipientName)))
            .EnsureSuccessStatusCode();
        return sale.SaleId;
    }

    [Fact]
    public async Task Only_sales_with_a_delivery_receipt_appear_and_totals_cover_the_whole_filtered_set()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var (b, session, variant) = await ArrangeAsync(branchId, price: 100m);

        await DeliverySaleAsync(b, session, variant, 100m, 60m); // charged delivery
        await DeliverySaleAsync(b, session, variant, 100m, 0m);  // free delivery
        // A normal sale with no delivery receipt at all — must never appear in the report.
        await CheckoutOkAsync(new CheckoutRequest(
            b, session, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variant, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));

        var result = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        result!.Page.TotalCount.Should().Be(2);
        result.Totals.TotalDeliveries.Should().Be(2);
        result.Totals.FreeDeliveries.Should().Be(1);
        result.Totals.ChargedDeliveries.Should().Be(1);
        result.Totals.TotalDeliveryCharges.Should().Be(60m);
        result.Totals.AverageDeliveryCharge.Should().Be(30m);
        result.Page.Items.Should().OnlyContain(r => r.DeliveryCharge == 60m || r.DeliveryCharge == 0m);
    }

    [Fact]
    public async Task Search_matches_recipient_name_and_related_sale_number()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var (b, session, variant) = await ArrangeAsync(branchId, price: 50m);

        var saleId = await DeliverySaleAsync(b, session, variant, 50m, 20m, recipientName: "Maria Santos");
        await DeliverySaleAsync(b, session, variant, 50m, 0m, recipientName: "Pedro Reyes");

        var byRecipient = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            "/api/reports/deliveries?search=Maria", TestJson.Options);
        byRecipient!.Page.Items.Should().ContainSingle(r => r.RecipientName == "Maria Santos");

        var saleNumber = (await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{saleId}", TestJson.Options))!.Sale.SaleNumber;
        var byNumber = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            $"/api/reports/deliveries?search={saleNumber}", TestJson.Options);
        byNumber!.Page.Items.Should().ContainSingle(r => r.SaleNumber == saleNumber);
    }

    [Fact]
    public async Task Newest_delivery_is_first_by_default()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var (b, session, variant) = await ArrangeAsync(branchId, price: 40m);

        await DeliverySaleAsync(b, session, variant, 40m, 10m, recipientName: "First");
        await DeliverySaleAsync(b, session, variant, 40m, 10m, recipientName: "Second");

        var result = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        result!.Page.Items[0].RecipientName.Should().Be("Second");
        result.Page.Items[1].RecipientName.Should().Be("First");
    }

    [Fact]
    public async Task Manager_is_forced_to_their_own_branch_owner_sees_tenant_wide()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainBranchId = await GetMainBranchIdAsync(owner);
        var other = await CreateBranchAsync("BGC", "BGC");

        var (mainBranch, mainSession, mainVariant) = await ArrangeAsync(mainBranchId, price: 30m);
        await DeliverySaleAsync(mainBranch, mainSession, mainVariant, 30m, 15m);

        // A user may only have one open register session at a time (CashierSessionOpen) — close the
        // first before the same Owner opens a second one on the other branch (same pattern as
        // ReportsTests.Owner_sees_tenant_wide_totals_manager_is_forced_to_their_own_branch).
        (await Client.PostAsJsonAsync($"/api/register-sessions/{mainSession}/close", new CloseRegisterSessionRequest(1200m)))
            .EnsureSuccessStatusCode();

        var (otherBranch, otherSession, otherVariant) = await ArrangeAsync(other.Id, price: 30m, categoryName: "Groceries");
        await DeliverySaleAsync(otherBranch, otherSession, otherVariant, 30m, 25m);

        var ownerResult = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        ownerResult!.Page.TotalCount.Should().Be(2);

        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainBranchId);
        Authorize(managerToken);
        var managerResult = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        managerResult!.Page.TotalCount.Should().Be(1);
        managerResult.Page.Items[0].DeliveryCharge.Should().Be(15m);
    }

    [Fact]
    public async Task Cashier_cannot_reach_the_delivery_report()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);

        var response = await Client.GetAsync("/api/reports/deliveries");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
