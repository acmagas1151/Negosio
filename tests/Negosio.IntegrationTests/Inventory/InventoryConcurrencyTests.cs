using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Inventory;

public class InventoryConcurrencyTests : IntegrationTest
{
    public InventoryConcurrencyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<(Guid BranchId, Guid ProductId)> SeedAsync()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id, sku: "CON-1");
        return (branchId, product.Product.Id);
    }

    [Fact]
    public async Task Adjusting_an_existing_row_without_a_token_is_a_bad_request()
    {
        var (branchId, productId) = await SeedAsync();
        await AdjustInventoryOkAsync(new AdjustInventoryRequest(branchId, productId, null, 10m, "Opening", null, null));

        var response = await AdjustInventoryAsync(new AdjustInventoryRequest(
            branchId, productId, null, 5m, "No token", null, ExpectedConcurrencyToken: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INVALID_INVENTORY_ADJUSTMENT");
    }

    [Fact]
    public async Task A_stale_token_yields_409_and_does_not_corrupt_the_quantity()
    {
        var (branchId, productId) = await SeedAsync();
        var opening = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 10m, "Opening", null, null));
        var staleToken = opening.ConcurrencyToken;

        // A first successful adjustment moves the row forward and returns a fresh token.
        var afterFirst = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 4m, "First", null, staleToken));
        afterFirst.QuantityOnHand.Should().Be(14m);
        afterFirst.ConcurrencyToken.Should().NotBe(staleToken);

        // Replaying the old token must be rejected.
        var conflict = await AdjustInventoryAsync(new AdjustInventoryRequest(
            branchId, productId, null, 7m, "Stale", null, staleToken));

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await conflict.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INVENTORY_CONCURRENCY_CONFLICT");

        var current = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>("/api/inventory", TestJson.Options);
        current!.Items.Single().QuantityOnHand.Should().Be(14m);

        // The fresh token still works.
        var recovered = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 1m, "Recovered", null, afterFirst.ConcurrencyToken));
        recovered.QuantityOnHand.Should().Be(15m);
    }

    [Fact]
    public async Task A_malformed_token_is_a_bad_request()
    {
        var (branchId, productId) = await SeedAsync();
        await AdjustInventoryOkAsync(new AdjustInventoryRequest(branchId, productId, null, 10m, "Opening", null, null));

        var response = await AdjustInventoryAsync(new AdjustInventoryRequest(
            branchId, productId, null, 5m, "Bad token", null, "not-base64!!"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
