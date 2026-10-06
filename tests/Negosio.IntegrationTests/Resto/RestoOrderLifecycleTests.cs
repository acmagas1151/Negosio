using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Common;
using Negosio.Application.Resto;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Resto;

public class RestoOrderLifecycleTests : RestoTestBase
{
    public RestoOrderLifecycleTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Voiding_a_kitchen_consumed_item_records_waste_once_and_settlement_bills_only_what_remains()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var kitchenUserId = await GetUserIdFromTokenAsync(setup.OwnerToken);

        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var firstRoundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, firstRoundId, setup, quantity: 2m);
        order = await ReleaseRoundAsync(order, firstRoundId);
        var voidedItemId = order.Rounds.Single().Items.Single().Id;
        await AcknowledgeItemAsync(voidedItemId, kitchenUserId);

        // A second released round keeps settlement unblocked and gives one line that is not voided.
        order = await AddRoundAsync(await GetOrderAsync(order.Id));
        var secondRoundId = order.Rounds.Single(r => r.Id != firstRoundId).Id;
        order = await AddItemAsync(order, secondRoundId, setup, quantity: 1m);
        order = await ReleaseRoundAsync(order, secondRoundId);

        var voidResponse = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/items/{voidedItemId}/void",
            new VoidRestoItemRequest(order.RowVersion, "Dropped plate", Approval: null));
        voidResponse.EnsureSuccessStatusCode();

        // Waste for the consumed 2 units is recorded at void time.
        (await GetInventoryQuantityAsync(setup.BranchId, setup.VariantId)).Should().Be(8m);

        var settled = await SettleAsync(await GetOrderAsync(order.Id), setup, Guid.NewGuid());

        // Only the remaining 1 unit is billed and deducted at settlement.
        (await GetInventoryQuantityAsync(setup.BranchId, setup.VariantId)).Should().Be(7m);
        var saleItemCount = await InScopeAsync(db => db.SaleItems.CountAsync(i => i.SaleId == settled.SaleId));
        saleItemCount.Should().Be(1);
    }

    [Fact]
    public async Task A_cashier_without_a_grant_cannot_void_a_consumed_item_without_manager_approval()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var kitchenUserId = await GetUserIdFromTokenAsync(setup.OwnerToken);

        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity: 2m);
        order = await ReleaseRoundAsync(order, roundId);
        var itemId = order.Rounds.Single().Items.Single().Id;
        await AcknowledgeItemAsync(itemId, kitchenUserId);

        var cashierToken = await AddTenantUserTokenAsync("cashier.nogrant@testco.com", UserRole.Cashier, setup.BranchId);
        Authorize(cashierToken);

        var response = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/items/{itemId}/void",
            new VoidRestoItemRequest(order.RowVersion, "Dropped plate", Approval: null));

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.RestoItemVoidApprovalRequired);
        (await GetInventoryQuantityAsync(setup.BranchId, setup.VariantId)).Should().Be(10m);
    }

    [Fact]
    public async Task A_second_open_bill_out_order_on_the_same_table_is_rejected_over_http()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        await OpenOrderAsync(setup);

        var response = await Client.PostAsJsonAsync("/api/resto/orders",
            new OpenRestoOrderRequest(RestoServiceType.BillOut, setup.SessionId, setup.BranchId, setup.TableId, "Table 1"));

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.RestoTableOccupied);
    }

    [Fact]
    public async Task A_return_against_a_resto_sale_is_rejected()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity: 1m);
        order = await ReleaseRoundAsync(order, roundId);
        var settled = await SettleAsync(order, setup, Guid.NewGuid());

        var saleItemId = await InScopeAsync(db => db.SaleItems
            .Where(i => i.SaleId == settled.SaleId).Select(i => i.Id).SingleAsync());

        var response = await Client.PostAsJsonAsync($"/api/sales/{settled.SaleId}/returns",
            new CreateReturnRequest(
                new[] { new ReturnLineInput(saleItemId, 1m) },
                "Customer changed mind", PaymentMethod.Cash, RefundReference: null, Approval: null));

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ReturnNotAllowedForResto);
    }

    [Fact]
    public async Task A_cashier_assigned_to_another_branch_cannot_see_the_order()
    {
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var order = await OpenOrderAsync(setup);

        var otherBranch = await CreateBranchAsync(name: "Branch B", code: "BRB");
        var otherCashierToken = await AddTenantUserTokenAsync("cashier.branchb@testco.com", UserRole.Cashier, otherBranch.Id);
        Authorize(otherCashierToken);

        var response = await Client.GetAsync($"/api/resto/orders/{order.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
