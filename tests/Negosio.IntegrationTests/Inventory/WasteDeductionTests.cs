using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Inventory;

public class WasteDeductionTests : IntegrationTest
{
    public WasteDeductionTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<(Guid TenantId, Guid BranchId, Guid VariantId, Guid UserId)> ArrangeAsync(decimal openingStock)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, openingStock: openingStock);
        return (login.User.TenantId, branchId, variantId, login.User.Id);
    }

    [Fact]
    public async Task Waste_reduces_stock_and_writes_one_waste_movement_referencing_the_source()
    {
        var (tenantId, branchId, variantId, userId) = await ArrangeAsync(openingStock: 10m);
        var referenceId = Guid.NewGuid();

        await InTenantScopeAsync(tenantId, async db =>
        {
            await new InventoryPosting(db).DeductForWasteAsync(
                tenantId, branchId, variantId, 3m, "RestoOrderItem", referenceId, "Prepared, then voided", userId);
            await db.SaveChangesAsync();
            return true;
        });

        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(7m);

        var movement = await InTenantScopeAsync(tenantId, db => db.StockMovements.AsNoTracking()
            .SingleAsync(m => m.Type == StockMovementType.Waste && m.ReferenceId == referenceId));
        movement.Quantity.Should().Be(-3m);
        movement.ReferenceType.Should().Be("RestoOrderItem");
        movement.Reason.Should().Be("Prepared, then voided");
    }

    [Fact]
    public async Task Waste_larger_than_available_stock_is_rejected_and_leaves_stock_unchanged()
    {
        var (tenantId, branchId, variantId, userId) = await ArrangeAsync(openingStock: 2m);

        var act = () => InTenantScopeAsync(tenantId, async db =>
        {
            await new InventoryPosting(db).DeductForWasteAsync(
                tenantId, branchId, variantId, 5m, "RestoOrder", Guid.NewGuid(), "Unpaid closure", userId);
            await db.SaveChangesAsync();
            return true;
        });

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be(ErrorCodes.InsufficientInventory);
        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(2m);
    }

    [Fact]
    public async Task Waste_for_a_product_with_no_inventory_row_is_rejected_and_never_creates_one()
    {
        var (tenantId, branchId, variantId, userId) = await ArrangeAsync(openingStock: 0m);

        var act = () => InTenantScopeAsync(tenantId, async db =>
        {
            await new InventoryPosting(db).DeductForWasteAsync(
                tenantId, branchId, variantId, 1m, "RestoOrder", Guid.NewGuid(), "Unpaid closure", userId);
            await db.SaveChangesAsync();
            return true;
        });

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be(ErrorCodes.InsufficientInventory);

        var rows = await InTenantScopeAsync(tenantId, db => db.BranchInventories.AsNoTracking()
            .CountAsync(i => i.ProductVariantId == variantId));
        rows.Should().Be(0, "waste must never fabricate stock where none was ever recorded");
    }
}
