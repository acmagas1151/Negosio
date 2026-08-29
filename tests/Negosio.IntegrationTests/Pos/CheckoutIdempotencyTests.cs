using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Pos;

public class CheckoutIdempotencyTests : IntegrationTest
{
    public CheckoutIdempotencyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Same_client_request_id_creates_only_one_sale_and_deducts_inventory_once()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (productId, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 40m, openingStock: 10m);

        var request = new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 3m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) });

        var first = await CheckoutOkAsync(request);
        var second = await CheckoutOkAsync(request);

        second.SaleId.Should().Be(first.SaleId);
        second.SaleNumber.Should().Be(first.SaleNumber);
        second.WasExistingRequest.Should().BeTrue();

        await InScopeAsync(async db =>
        {
            (await db.Sales.CountAsync()).Should().Be(1);
            (await db.StockMovements.CountAsync(m => m.Type == StockMovementType.Sale)).Should().Be(1);
            (await db.BranchInventories.Where(i => i.ProductVariantId == variantId).Select(i => i.QuantityOnHand).SingleAsync())
                .Should().Be(7m);
            return true;
        });
    }
}
