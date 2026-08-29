using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Catalog;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Catalog;

public class VariantTests : IntegrationTest
{
    public VariantTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Adding_the_first_variant_promotes_the_default_and_flips_hasVariants()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id, "Iced Coffee", sku: "ICED");

        var create = await Client.PostAsJsonAsync(
            $"/api/products/{product.Product.Id}/variants",
            new VariantInput("Small", "ICED-S", null, 30m, 60m));
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await Client.PostAsJsonAsync(
            $"/api/products/{product.Product.Id}/variants",
            new VariantInput("Large", "ICED-L", null, 40m, 80m));
        second.EnsureSuccessStatusCode();

        var detail = await Client.GetFromJsonAsync<ProductDetailDto>(
            $"/api/products/{product.Product.Id}", TestJson.Options);
        detail!.Product.HasVariants.Should().BeTrue();
        detail.Variants.Should().HaveCount(2);
        detail.Variants.Should().OnlyContain(v => !v.IsDefault);
    }

    [Fact]
    public async Task Variant_cannot_be_attached_to_another_tenants_product()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var categoryB = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(categoryB.Id);

        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await Client.PostAsJsonAsync(
            $"/api/products/{product.Product.Id}/variants",
            new VariantInput("Sneaky", "SNK-1", null, 1m, 2m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Updating_an_unknown_variant_returns_not_found()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id);

        var response = await Client.PutAsJsonAsync(
            $"/api/products/{product.Product.Id}/variants/{Guid.NewGuid()}",
            new VariantInput("Ghost", null, null, 1m, 2m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("VARIANT_NOT_FOUND");
    }

    [Fact]
    public async Task Last_active_variant_cannot_be_deactivated()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id, sku: "ONE");

        var created = await Client.PostAsJsonAsync(
            $"/api/products/{product.Product.Id}/variants",
            new VariantInput("Only", "ONLY-1", null, 10m, 20m));
        var variant = (await created.Content.ReadFromJsonAsync<ProductVariantDto>(TestJson.Options))!;

        var response = await Client.DeleteAsync($"/api/products/{product.Product.Id}/variants/{variant.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("PRODUCT_HAS_VARIANTS");
    }
}
