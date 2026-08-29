using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Pos;

public class CheckoutConcurrencyTests : IntegrationTest
{
    public CheckoutConcurrencyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Two_checkouts_for_the_final_unit_leave_stock_at_zero_never_negative()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 1m);

        CheckoutRequest Build() => new(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) });

        // Two independent clients hit the last unit at the same time.
        var a = Client.PostAsJsonAsync("/api/pos/checkout", Build());
        var b = Client.PostAsJsonAsync("/api/pos/checkout", Build());
        var responses = await Task.WhenAll(a, b);

        responses.Count(r => r.IsSuccessStatusCode).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        await InScopeAsync(async db =>
        {
            var qty = await db.BranchInventories.Where(i => i.ProductVariantId == variantId).Select(i => i.QuantityOnHand).SingleAsync();
            qty.Should().Be(0m);
            qty.Should().BeGreaterThanOrEqualTo(0m);

            (await db.Sales.CountAsync()).Should().Be(1);
            (await db.StockMovements.CountAsync(m => m.Type == StockMovementType.Sale)).Should().Be(1);
            return true;
        });
    }
}
