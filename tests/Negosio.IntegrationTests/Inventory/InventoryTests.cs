using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Inventory;

public class InventoryTests : IntegrationTest
{
    public InventoryTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<(Guid BranchId, Guid ProductId)> SeedAsync(
        string sku = "SKU-1", string productName = "Coke 1.5L", bool trackInventory = true)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var category = await CreateCategoryAsync();
        var product = await CreateSimpleProductAsync(category.Id, productName, sku: sku, trackInventory: trackInventory);
        return (branchId, product.Product.Id);
    }

    [Fact]
    public async Task Opening_inventory_succeeds_and_creates_the_row_without_a_token()
    {
        var (branchId, productId) = await SeedAsync();

        var row = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 50m, "Opening stock count", ReorderLevel: 10m, ExpectedConcurrencyToken: null));

        row.QuantityOnHand.Should().Be(50m);
        row.ReorderLevel.Should().Be(10m);
        row.Status.Should().Be(InventoryStatus.InStock);
        row.ConcurrencyToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Adjustment_creates_a_stock_movement_with_before_and_after()
    {
        var (branchId, productId) = await SeedAsync();
        var opening = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 20m, "Opening", null, null));

        await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 5m, "Found extra", null, opening.ConcurrencyToken));

        var movements = await Client.GetFromJsonAsync<PagedResult<StockMovementDto>>(
            "/api/inventory/movements", TestJson.Options);
        movements!.TotalCount.Should().Be(2);
        var latest = movements.Items.First();
        latest.Quantity.Should().Be(5m);
        latest.QuantityBefore.Should().Be(20m);
        latest.QuantityAfter.Should().Be(25m);
        latest.Reason.Should().Be("Found extra");
        latest.CreatedByName.Should().Contain("Ace");
    }

    [Fact]
    public async Task Increase_and_decrease_update_the_quantity()
    {
        var (branchId, productId) = await SeedAsync();
        var row = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 30m, "Opening", null, null));

        row = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 12m, "Restock", null, row.ConcurrencyToken));
        row.QuantityOnHand.Should().Be(42m);

        row = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, -20m, "Damaged", null, row.ConcurrencyToken));
        row.QuantityOnHand.Should().Be(22m);
    }

    [Fact]
    public async Task Inventory_cannot_go_below_zero()
    {
        var (branchId, productId) = await SeedAsync();
        var row = await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, productId, null, 5m, "Opening", null, null));

        var response = await AdjustInventoryAsync(new AdjustInventoryRequest(
            branchId, productId, null, -10m, "Oops", null, row.ConcurrencyToken));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INSUFFICIENT_INVENTORY");

        var unchanged = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>("/api/inventory", TestJson.Options);
        unchanged!.Items.Single().QuantityOnHand.Should().Be(5m);
    }

    [Fact]
    public async Task Adjustment_is_rejected_for_a_non_tracked_product()
    {
        var (branchId, productId) = await SeedAsync(sku: "NT-1", trackInventory: false);

        var response = await AdjustInventoryAsync(new AdjustInventoryRequest(
            branchId, productId, null, 5m, "Opening", null, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INVALID_INVENTORY_ADJUSTMENT");
    }

    [Fact]
    public async Task Invalid_branch_is_rejected()
    {
        var (_, productId) = await SeedAsync();

        var response = await AdjustInventoryAsync(new AdjustInventoryRequest(
            Guid.NewGuid(), productId, null, 5m, "Opening", null, null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("BRANCH_NOT_FOUND");
    }

    [Fact]
    public async Task Tenant_cannot_adjust_another_tenants_inventory()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var branchB = await GetMainBranchIdAsync(tenantB);
        var categoryB = await CreateCategoryAsync();
        var productB = await CreateSimpleProductAsync(categoryB.Id, sku: "B-1");

        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await AdjustInventoryAsync(new AdjustInventoryRequest(
            branchB, productB.Product.Id, null, 5m, "Opening", null, null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Low_stock_filter_returns_only_items_at_or_below_reorder_level()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var category = await CreateCategoryAsync();
        var lowProduct = await CreateSimpleProductAsync(category.Id, "Low", sku: "LOW-1");
        var okProduct = await CreateSimpleProductAsync(category.Id, "Plenty", sku: "OK-1");

        await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, lowProduct.Product.Id, null, 3m, "Opening", ReorderLevel: 5m, ExpectedConcurrencyToken: null));
        await AdjustInventoryOkAsync(new AdjustInventoryRequest(
            branchId, okProduct.Product.Id, null, 100m, "Opening", ReorderLevel: 5m, ExpectedConcurrencyToken: null));

        var low = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>(
            "/api/inventory?lowStock=true", TestJson.Options);

        low!.Items.Should().ContainSingle(i => i.ProductName == "Low");
        low.Items.Single().Status.Should().Be(InventoryStatus.LowStock);
    }

    [Fact]
    public async Task Movement_history_is_tenant_scoped()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var branchB = await GetMainBranchIdAsync(tenantB);
        var categoryB = await CreateCategoryAsync();
        var productB = await CreateSimpleProductAsync(categoryB.Id, sku: "B-1");
        await AdjustInventoryOkAsync(new AdjustInventoryRequest(branchB, productB.Product.Id, null, 10m, "Opening", null, null));

        var tenantA = await RegisterAndLoginAsync(
            NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var movements = await Client.GetFromJsonAsync<PagedResult<StockMovementDto>>(
            "/api/inventory/movements", TestJson.Options);

        movements!.TotalCount.Should().Be(0);
    }
}
