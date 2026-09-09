using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class ReceiptTests : IntegrationTest
{
    public ReceiptTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record SaleScene(Guid SaleId, decimal ExpectedGrandTotal, decimal CashReceived);

    /// <summary>One cash sale: qty 1 @ 100, cash tendered 200 (change 100).</summary>
    private async Task<SaleScene> ArrangeSaleAsync()
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
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));

        return new SaleScene(sale.SaleId, sale.GrandTotal, 200m);
    }

    [Fact] // acceptance 8, 10, 11, 12
    public async Task Receipt_with_no_settings_configured_uses_safe_defaults()
    {
        var scene = await ArrangeSaleAsync();

        var receipt = await Client.GetFromJsonAsync<ReceiptDto>($"/api/sales/{scene.SaleId}/receipt", TestJson.Options);

        receipt!.HeaderText.Should().BeNull();
        receipt.FooterText.Should().BeNull();
        receipt.ShowBranch.Should().BeTrue();
        receipt.ShowReferenceNumber.Should().BeTrue();
        receipt.Width.Should().Be(ReceiptWidth.Mm80);
        receipt.GrandTotal.Should().Be(scene.ExpectedGrandTotal);
        receipt.Payments[0].ReceivedAmount.Should().Be(scene.CashReceived);
    }

    [Fact] // acceptance 7, 13
    public async Task Receipt_reflects_saved_header_and_footer()
    {
        var scene = await ArrangeSaleAsync();

        (await Client.PutAsJsonAsync("/api/settings/receipts",
            new UpdateReceiptSettingsRequest(ReceiptWidth.Mm58, "MY STORE", "No refunds",
                true, false, true, true, false, null, null, true, true, true, true)))
            .EnsureSuccessStatusCode();

        var receipt = await Client.GetFromJsonAsync<ReceiptDto>($"/api/sales/{scene.SaleId}/receipt", TestJson.Options);

        receipt!.HeaderText.Should().Be("MY STORE");
        receipt.FooterText.Should().Be("No refunds");
        receipt.ShowCashier.Should().BeFalse();
        receipt.ShowReferenceNumber.Should().BeFalse();
        receipt.Width.Should().Be(ReceiptWidth.Mm58);
    }
}
