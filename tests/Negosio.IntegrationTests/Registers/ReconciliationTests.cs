using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

public class ReconciliationTests : IntegrationTest
{
    public ReconciliationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Expected_cash_accounts_for_opening_sales_void_refund_and_cash_movements()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantA) = await SeedStockedProductAsync(branchId, category.Id, "Product A", "SKU-A", sellingPrice: 500m, openingStock: 20m);
        var (_, variantB) = await SeedStockedProductAsync(branchId, category.Id, "Product B", "SKU-B", sellingPrice: 200m, openingStock: 20m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 5000m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        // Sale 1: ₱500 cash — this one gets voided.
        var sale1 = (await (await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantA, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) })))
            .Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        // Sale 2: ₱200 cash — this one stays completed.
        await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantB, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));

        var cashInRes = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 500m, "Float top-up"));
        cashInRes.EnsureSuccessStatusCode();
        var cashOutRes = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashOut, 1000m, "Bank deposit"));
        cashOutRes.EnsureSuccessStatusCode();

        // Voiding requires Owner/Admin/Manager (or an explicit grant) — the cashier who rang up the
        // sale cannot self-approve, so this switches to the owner's token for just this one call.
        Authorize(owner.AccessToken);
        var voidRes = await Client.PostAsJsonAsync($"/api/sales/{sale1.SaleId}/void", new VoidSaleRequest("Wrong item rung up"));
        voidRes.EnsureSuccessStatusCode();

        // Only the cashier who opened the session may close it — switch back before closing.
        Authorize(cashierToken);
        var closeRes = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/close",
            new CloseRegisterSessionRequest(4700m));
        closeRes.EnsureSuccessStatusCode();
        var closed = (await closeRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        // 5000 opening + 700 gross cash sales (500 + 200) - 500 voided - 0 refunds + 500 cash in - 1000 cash out = 4700
        closed.GrossCashSales.Should().Be(700m);
        closed.VoidedCashSales.Should().Be(500m);
        closed.RefundCashOut.Should().Be(0m);
        closed.CashIn.Should().Be(500m);
        closed.CashOut.Should().Be(1000m);
        closed.ExpectedCash.Should().Be(4700m);
        closed.ClosingCash.Should().Be(4700m);
        closed.CashDifference.Should().Be(0m);
    }
}
