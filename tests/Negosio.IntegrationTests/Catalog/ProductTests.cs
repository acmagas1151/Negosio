using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Application.Catalog;
using Negosio.Application.Common;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Catalog;

public class ProductTests : IntegrationTest
{
    public ProductTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Product_creation_succeeds_and_creates_a_hidden_default_variant()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();

        var product = await CreateSimpleProductAsync(category.Id, "Coke 1.5L", sku: "COKE-15", costPrice: 12m, sellingPrice: 25m);

        product.Product.HasVariants.Should().BeFalse();
        product.Product.Sku.Should().Be("COKE-15");
        product.Product.MinSellingPrice.Should().Be(25m);
        product.Product.MaxSellingPrice.Should().Be(25m);
        product.Variants.Should().ContainSingle(v => v.IsDefault);
    }

    [Fact]
    public async Task Product_requires_a_category_from_the_same_tenant()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var foreignCategory = await CreateCategoryAsync("Foreign");

        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await Client.PostAsJsonAsync("/api/products", new CreateProductRequest(
            foreignCategory.Id, "Ghost", null, true, null, null, 1m, 2m, null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("CATEGORY_NOT_FOUND");
    }

    [Fact]
    public async Task Duplicate_sku_in_the_same_tenant_is_rejected()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();
        await CreateSimpleProductAsync(category.Id, "First", sku: "DUP-1");

        var response = await Client.PostAsJsonAsync("/api/products", new CreateProductRequest(
            category.Id, "Second", null, true, "DUP-1", null, 1m, 2m, null));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("SKU_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Duplicate_barcode_in_the_same_tenant_is_rejected()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();
        await CreateSimpleProductAsync(category.Id, "First", barcode: "480000000001");

        var response = await Client.PostAsJsonAsync("/api/products", new CreateProductRequest(
            category.Id, "Second", null, true, null, "480000000001", 1m, 2m, null));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("BARCODE_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Same_sku_across_tenants_succeeds()
    {
        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);
        var categoryA = await CreateCategoryAsync();
        await CreateSimpleProductAsync(categoryA.Id, "A product", sku: "SHARED-1");

        var tenantB = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "B Co", email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var categoryB = await CreateCategoryAsync();

        var response = await Client.PostAsJsonAsync("/api/products", new CreateProductRequest(
            categoryB.Id, "B product", null, true, "SHARED-1", null, 1m, 2m, null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Tenant_A_cannot_read_tenant_B_product()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var categoryB = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(categoryB.Id);

        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await Client.GetAsync($"/api/products/{product.Product.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_supports_search_pagination_and_category_filter()
    {
        await RegisterLoginAndAuthorizeAsync();
        var drinks = await CreateCategoryAsync("Drinks");
        var snacks = await CreateCategoryAsync("Snacks");
        await CreateSimpleProductAsync(drinks.Id, "Iced Coffee", sku: "ICED-1");
        await CreateSimpleProductAsync(drinks.Id, "Hot Coffee", sku: "HOT-1");
        await CreateSimpleProductAsync(snacks.Id, "Chips", sku: "CHIP-1");

        var bySearch = await Client.GetFromJsonAsync<PagedResult<ProductDto>>(
            "/api/products?search=coffee", TestJson.Options);
        bySearch!.TotalCount.Should().Be(2);

        var bySku = await Client.GetFromJsonAsync<PagedResult<ProductDto>>(
            "/api/products?search=CHIP-1", TestJson.Options);
        bySku!.Items.Should().ContainSingle(p => p.Name == "Chips");

        var byCategory = await Client.GetFromJsonAsync<PagedResult<ProductDto>>(
            $"/api/products?categoryId={drinks.Id}&pageSize=1", TestJson.Options);
        byCategory!.TotalCount.Should().Be(2);
        byCategory.Items.Should().HaveCount(1);
        byCategory.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task Multi_variant_product_reports_a_price_range_and_sorts_by_minimum()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();

        var withVariants = await Client.PostAsJsonAsync("/api/products", new CreateProductRequest(
            category.Id, "T-Shirt", null, true, null, null, 0m, 0m,
            new List<VariantInput>
            {
                new("Small", "TS-S", null, 100m, 199m),
                new("Large", "TS-L", null, 120m, 249m),
            }));
        withVariants.EnsureSuccessStatusCode();

        await CreateSimpleProductAsync(category.Id, "Cheap Cap", sku: "CAP-1", sellingPrice: 50m);

        var detail = await withVariants.Content.ReadFromJsonAsync<ProductDetailDto>(TestJson.Options);
        detail!.Product.HasVariants.Should().BeTrue();
        detail.Product.MinSellingPrice.Should().Be(199m);
        detail.Product.MaxSellingPrice.Should().Be(249m);
        detail.Product.Sku.Should().BeNull();

        var sorted = await Client.GetFromJsonAsync<PagedResult<ProductDto>>(
            "/api/products?sortBy=sellingPrice&sortDirection=asc", TestJson.Options);
        sorted!.Items.First().Name.Should().Be("Cheap Cap");
    }

    [Fact]
    public async Task Cost_price_is_hidden_from_non_management_roles()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();
        await CreateSimpleProductAsync(category.Id, "Priced", sku: "PRICED-1", costPrice: 30m, sellingPrice: 60m);

        var ownerView = await Client.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products", TestJson.Options);
        ownerView!.Items.Single().MinCostPrice.Should().Be(30m);

        var cashierToken = await AddTenantUserTokenAsync("cashier@example.com", UserRole.Cashier);

        Authorize(cashierToken);
        var cashierView = await Client.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products", TestJson.Options);
        cashierView!.Items.Single().MinCostPrice.Should().BeNull();
        cashierView.Items.Single().MinSellingPrice.Should().Be(60m);
    }

    [Fact]
    public async Task Non_writer_role_cannot_create_products()
    {
        await RegisterLoginAndAuthorizeAsync();

        var viewerToken = await AddTenantUserTokenAsync("viewer@example.com", UserRole.Viewer);
        var category = await CreateCategoryAsync();

        Authorize(viewerToken);
        var response = await Client.PostAsJsonAsync("/api/products", new CreateProductRequest(
            category.Id, "Nope", null, true, null, null, 1m, 2m, null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
