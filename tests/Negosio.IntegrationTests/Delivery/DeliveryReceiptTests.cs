using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptTests : IntegrationTest
{
    public DeliveryReceiptTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record DrScene(Guid SaleId, string SaleNumber, Guid ProductId);

    private static CreateDeliveryReceiptRequest Req(IReadOnlyList<CreateDeliveryReceiptItemInput>? items = null) =>
        new("Juan Dela Cruz", "123 Ayala Ave, Makati", "0917 111 2222", "Leave at guardhouse", items);

    /// <summary>One completed cash sale: <paramref name="qty"/> @ <paramref name="price"/>, tendered with change.</summary>
    private async Task<DrScene> ArrangeSaleAsync(decimal qty = 1m, decimal price = 100m)
    {
        if (CurrentTenantId == Guid.Empty)
        {
            await RegisterLoginAndAuthorizeAsync();
        }

        var branchId = await InScopeAsync(db => db.Branches
            .OrderBy(b => b.CreatedAtUtc).Select(b => b.Id).FirstAsync());
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (productId, variantId) = await SeedStockedProductAsync(
            branchId, category.Id, sellingPrice: price, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + 100m) }));

        return new DrScene(sale.SaleId, sale.SaleNumber, productId);
    }

    private Task RenameProductAsync(Guid productId, string newName) =>
        InScopeAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Products SET Name = {newName} WHERE Id = {productId}");
            return true;
        });

    [Fact] // acceptance 14, 16, 17
    public async Task Create_from_sale_persists_and_snapshots()
    {
        var scene = await ArrangeSaleAsync(qty: 2m, price: 50m);
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await created.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        dr.Id.Should().NotBeEmpty();
        dr.RecipientName.Should().Be("Juan Dela Cruz");
        dr.DeliveryAddress.Should().Be("123 Ayala Ave, Makati");
        dr.RelatedSaleNumber.Should().Be(scene.SaleNumber);
        dr.PreparedByName.Should().NotBeNullOrEmpty();
        dr.Items.Should().ContainSingle();
        dr.Items[0].Quantity.Should().Be(2m);
    }

    [Fact]
    public async Task Create_rejects_blank_recipient_name_or_address()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt",
            new CreateDeliveryReceiptRequest("  ", "123 Ayala Ave", null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt",
            new CreateDeliveryReceiptRequest("Juan Dela Cruz", "   ", null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // acceptance 20
    public async Task Second_post_for_same_sale_returns_the_same_document()
    {
        var scene = await ArrangeSaleAsync();
        var first = await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options);
        var second = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req());
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondDto = (await second.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        secondDto.Id.Should().Be(first!.Id);
    }

    [Fact] // acceptance 18
    public async Task Prices_are_hidden_when_DeliveryShowPrices_is_off()
    {
        var scene = await ArrangeSaleAsync(price: 50m);
        await Client.PutAsJsonAsync("/api/settings/receipts", new UpdateReceiptSettingsRequest(
            ReceiptWidth.Mm80, null, null, true, true, true, true, true,
            "DELIVERY RECEIPT", "Please inspect on receipt",
            DeliveryShowPrices: false, DeliveryShowRelatedSaleNumber: true,
            DeliveryShowContactNumber: true, DeliveryShowSignatureFields: true));

        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        dr.ShowPrices.Should().BeFalse();
        dr.Items[0].UnitPrice.Should().BeNull();
        dr.Items[0].Amount.Should().BeNull();
        dr.HeaderText.Should().Be("DELIVERY RECEIPT");
        dr.FooterText.Should().Be("Please inspect on receipt");
    }

    [Fact] // acceptance: reprint stable
    public async Task Get_by_id_returns_same_snapshot_after_catalog_rename()
    {
        var scene = await ArrangeSaleAsync();
        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        await RenameProductAsync(scene.ProductId, "Totally Different Name");
        var again = await Client.GetFromJsonAsync<DeliveryReceiptDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        again!.Id.Should().Be(dr.Id);
        again.Items[0].ProductName.Should().Be(dr.Items[0].ProductName);
    }

    [Fact] // fix round 1: a permanent DR must never be created line-less
    public async Task Create_rejects_empty_item_selection()
    {
        var scene = await ArrangeSaleAsync();
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt",
            Req(Array.Empty<CreateDeliveryReceiptItemInput>()));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // fix round 1: two lines for one sale item could aggregate past the sold quantity
    public async Task Create_rejects_duplicate_sale_item()
    {
        var scene = await ArrangeSaleAsync(qty: 2m);
        var sale = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{scene.SaleId}", TestJson.Options);
        var saleItemId = sale!.Items[0].Id;

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req(new[]
        {
            new CreateDeliveryReceiptItemInput(saleItemId, 1m),
            new CreateDeliveryReceiptItemInput(saleItemId, 1m),
        }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Branch_scoped_user_cannot_read_another_branchs_DR()
    {
        await RegisterLoginAndAuthorizeAsync();
        var scene = await ArrangeSaleAsync();
        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        var other = await CreateBranchAsync("BGC", "BGC");
        Authorize(await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, other.Id));
        (await Client.GetAsync($"/api/delivery-receipts/{dr.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
