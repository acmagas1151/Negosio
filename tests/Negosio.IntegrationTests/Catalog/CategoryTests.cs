using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Catalog;
using Negosio.Application.Common;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Catalog;

public class CategoryTests : IntegrationTest
{
    public CategoryTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Tenant_can_create_a_category()
    {
        await RegisterLoginAndAuthorizeAsync();

        var response = await Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest("Beverages", "Drinks"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CategoryDto>(TestJson.Options);
        body!.Name.Should().Be("Beverages");
        body.IsActive.Should().BeTrue();
        body.ProductCount.Should().Be(0);
    }

    [Theory]
    [InlineData("Beverages", "beverages")]
    [InlineData("Beverages", "  BEVERAGES  ")]
    [InlineData("Frozen Goods", "frozen  goods")]
    public async Task Duplicate_category_name_in_same_tenant_is_rejected(string first, string second)
    {
        await RegisterLoginAndAuthorizeAsync();
        await CreateCategoryAsync(first);

        var response = await Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(second, null));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("CATEGORY_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Same_category_name_across_tenants_succeeds()
    {
        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);
        await CreateCategoryAsync("Beverages");

        var tenantB = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "Other Co", email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);

        var response = await Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest("Beverages", null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Tenant_A_cannot_fetch_tenant_B_category()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var category = await CreateCategoryAsync("Snacks");

        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await Client.GetAsync($"/api/categories/{category.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("CATEGORY_NOT_FOUND");
    }

    [Fact]
    public async Task Tenant_A_cannot_update_tenant_B_category()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var category = await CreateCategoryAsync("Snacks");

        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await Client.PutAsJsonAsync(
            $"/api/categories/{category.Id}",
            new UpdateCategoryRequest("Hijacked", null, true));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_deactivates_the_category()
    {
        await RegisterLoginAndAuthorizeAsync();
        var category = await CreateCategoryAsync("Temporary");

        var deleteResponse = await Client.DeleteAsync($"/api/categories/{category.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await Client.GetAsync($"/api/categories/{category.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResponse.Content.ReadFromJsonAsync<CategoryDto>(TestJson.Options);
        body!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task List_paginates_and_filters_by_active()
    {
        await RegisterLoginAndAuthorizeAsync();
        var first = await CreateCategoryAsync("Alpha");
        await CreateCategoryAsync("Bravo");
        await Client.DeleteAsync($"/api/categories/{first.Id}");

        var activeOnly = await Client.GetFromJsonAsync<PagedResult<CategoryDto>>(
            "/api/categories?isActive=true&page=1&pageSize=1", TestJson.Options);

        activeOnly!.TotalCount.Should().Be(1);
        activeOnly.Items.Should().ContainSingle(c => c.Name == "Bravo");
        activeOnly.PageSize.Should().Be(1);
    }
}
