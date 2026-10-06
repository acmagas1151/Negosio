using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Common;
using Negosio.Application.Resto;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Resto;

public class RestoSettlementTests : RestoTestBase
{
    public RestoSettlementTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Settling_a_released_bill_out_order_creates_one_resto_sale_and_deducts_stock_once()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity: 2m);
        order = await ReleaseRoundAsync(order, roundId);

        var settlementRequestId = Guid.NewGuid();
        var result = await SettleAsync(order, setup, settlementRequestId);

        result.WasExisting.Should().BeFalse();
        result.GrandTotal.Should().BeGreaterThan(0m);
        result.AmountPaid.Should().Be(result.GrandTotal);
        (await GetInventoryQuantityAsync(setup.BranchId, setup.VariantId)).Should().Be(8m);

        var sale = await InScopeAsync(db => db.Sales.AsNoTracking()
            .Where(s => s.Id == result.SaleId)
            .Select(s => new { s.Origin, s.Status, s.ClientRequestId })
            .SingleAsync());
        sale.Origin.Should().Be(SaleOrigin.Resto);
        sale.Status.Should().Be(SaleStatus.Completed);
        sale.ClientRequestId.Should().Be(settlementRequestId);

        var settled = await GetOrderAsync(order.Id);
        settled.Status.Should().Be(RestoOrderStatus.Settled);
        settled.SaleId.Should().Be(result.SaleId);
    }

    [Fact]
    public async Task Retrying_a_settlement_with_the_same_key_returns_the_original_sale_without_deducting_again()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity: 2m);
        order = await ReleaseRoundAsync(order, roundId);

        var settlementRequestId = Guid.NewGuid();
        var first = await SettleAsync(order, setup, settlementRequestId);

        // The retry carries the stale RowVersion, as a client that lost the first response would.
        var retry = await SettleAsync(order, setup, settlementRequestId);

        retry.WasExisting.Should().BeTrue();
        retry.SaleId.Should().Be(first.SaleId);
        (await GetInventoryQuantityAsync(setup.BranchId, setup.VariantId)).Should().Be(8m);

        var saleCount = await InScopeAsync(db => db.Sales.CountAsync());
        saleCount.Should().Be(1);
    }

    [Fact]
    public async Task A_bill_out_order_with_an_unreleased_round_cannot_be_settled()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity: 1m);

        var response = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/settle", SettleBody(order, setup, Guid.NewGuid()));

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.RestoDraftRoundsPending);
        (await GetInventoryQuantityAsync(setup.BranchId, setup.VariantId)).Should().Be(10m);
    }

    [Fact]
    public async Task Unpaid_close_records_no_sale_and_leaves_stock_alone_when_nothing_was_consumed()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity: 2m);
        order = await ReleaseRoundAsync(order, roundId);

        var response = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/unpaid-close",
            new UnpaidCloseRestoOrderRequest(order.RowVersion, Guid.NewGuid(), "Walked out", Approval: null));
        response.EnsureSuccessStatusCode();
        var closed = await response.Content.ReadFromJsonAsync<RestoOrderDto>(TestJson.Options);

        closed!.Status.Should().Be(RestoOrderStatus.UnpaidClosed);
        closed.SaleId.Should().BeNull();

        var saleCount = await InScopeAsync(db => db.Sales.CountAsync());
        saleCount.Should().Be(0);
        (await GetInventoryQuantityAsync(setup.BranchId, setup.VariantId)).Should().Be(10m);
    }
}
