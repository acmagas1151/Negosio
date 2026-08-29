using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Pos;

public class PosCatalogTests : IntegrationTest
{
    public PosCatalogTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Search_matches_name_and_sku_and_hides_cost()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var category = await CreateCategoryAsync();
        await SeedStockedProductAsync(branchId, category.Id, "Iced Coffee", "ICED-1", sellingPrice: 90m, costPrice: 30m, openingStock: 12m);
        await SeedStockedProductAsync(branchId, category.Id, "Brewed Coffee", "BREW-1", openingStock: 0m);

        var byName = await Client.GetFromJsonAsync<PagedResult<PosCatalogItemDto>>(
            $"/api/pos/catalog?branchId={branchId}&search=coffee", TestJson.Options);
        byName!.TotalCount.Should().Be(2);

        var bySku = await Client.GetFromJsonAsync<PagedResult<PosCatalogItemDto>>(
            $"/api/pos/catalog?branchId={branchId}&search=ICED-1", TestJson.Options);
        var iced = bySku!.Items.Single();
        iced.ProductName.Should().Be("Iced Coffee");
        iced.SellingPrice.Should().Be(90m);
        iced.QuantityAvailable.Should().Be(12m);
        iced.IsAvailable.Should().BeTrue();

        // The raw payload must not carry any cost/margin field.
        var raw = await Client.GetStringAsync($"/api/pos/catalog?branchId={branchId}&search=ICED-1");
        using var doc = JsonDocument.Parse(raw);
        var item = doc.RootElement.GetProperty("items")[0];
        item.TryGetProperty("costPrice", out _).Should().BeFalse();
        item.TryGetProperty("cost", out _).Should().BeFalse();
        item.TryGetProperty("margin", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Barcode_lookup_is_exact_and_tenant_scoped()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id, "Chips", sku: "CHIP-1", barcode: "4800000000001");

        var found = await Client.GetFromJsonAsync<PosCatalogItemDto>(
            $"/api/pos/catalog/barcode/4800000000001?branchId={branchId}", TestJson.Options);
        found!.ProductId.Should().Be(product.Product.Id);

        var missing = await Client.GetAsync($"/api/pos/catalog/barcode/9999999999999?branchId={branchId}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Inactive_products_are_excluded()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id, "Retired", sku: "OLD-1");
        await Client.DeleteAsync($"/api/products/{product.Product.Id}");

        var result = await Client.GetFromJsonAsync<PagedResult<PosCatalogItemDto>>(
            $"/api/pos/catalog?branchId={branchId}&search=Retired", TestJson.Options);

        result!.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Catalog_is_tenant_isolated()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var branchB = await GetMainBranchIdAsync(tenantB);
        var categoryB = await CreateCategoryAsync();
        await SeedStockedProductAsync(branchB, categoryB.Id, "Secret", "SECRET-1");

        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);
        var branchA = await GetMainBranchIdAsync(tenantA);

        var response = await Client.GetAsync($"/api/pos/catalog?branchId={branchB}&search=Secret");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound); // branchB is not tenant A's

        var ownBranch = await Client.GetFromJsonAsync<PagedResult<PosCatalogItemDto>>(
            $"/api/pos/catalog?branchId={branchA}&search=Secret", TestJson.Options);
        ownBranch!.TotalCount.Should().Be(0);
    }
}
