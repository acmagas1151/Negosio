using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Catalog;
using Negosio.Application.Pos;
using Negosio.Application.Auth;
using Negosio.Application.Resto;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Resto;

/// <summary>Shared fixture helpers for RestoPOS HTTP tests: a Bill-Out order on a stocked product, driven through the API.</summary>
public abstract class RestoTestBase : IntegrationTest
{
    protected RestoTestBase(NegosioApiFactory factory) : base(factory)
    {
    }

    protected sealed record Setup(Guid BranchId, Guid TableId, Guid StationId, Guid VariantId, Guid SessionId, string OwnerToken, Guid TenantId);

    protected async Task<Setup> SetupBillOutAsync(decimal openingStock)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var tenantId = login.User.TenantId;

        var (tableId, stationId) = await InScopeAsync(async db =>
        {
            var branch = await db.Branches.SingleAsync(b => b.Id == branchId);
            branch.ConfigureRestoServiceTypes(supportsPayAsYouOrder: true, supportsBillOut: true);

            var table = RestoTable.Create(tenantId, branchId, "Table 1");
            var station = RestoStation.Create(tenantId, branchId, "Kitchen");
            db.RestoTables.Add(table);
            db.RestoStations.Add(station);
            await db.SaveChangesAsync();
            return (table.Id, station.Id);
        });

        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, openingStock: openingStock);

        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);

        return new Setup(branchId, tableId, stationId, variantId, session.Id, login.AccessToken, tenantId);
    }

    /// <summary>A Pay-as-you-order setup: no table, one stocked product. A different <paramref name="request"/> creates another tenant.</summary>
    protected async Task<Setup> SetupPayoAsync(decimal openingStock, RegisterRequest? request = null)
    {
        var login = await RegisterLoginAndAuthorizeAsync(request);
        var branchId = await GetMainBranchIdAsync(login);
        var tenantId = login.User.TenantId;

        var stationId = await InScopeAsync(async db =>
        {
            var branch = await db.Branches.SingleAsync(b => b.Id == branchId);
            branch.ConfigureRestoServiceTypes(supportsPayAsYouOrder: true, supportsBillOut: true);
            var station = RestoStation.Create(tenantId, branchId, "Kitchen");
            db.RestoStations.Add(station);
            await db.SaveChangesAsync();
            return station.Id;
        });

        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, openingStock: openingStock);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);

        return new Setup(branchId, Guid.Empty, stationId, variantId, session.Id, login.AccessToken, tenantId);
    }

    /// <summary>Opens, adds one item, and settles a Pay-as-you-order order. Its single round stays Draft for the worker.</summary>
    protected async Task<RestoOrderDto> SettlePayoOrderAsync(Setup setup, decimal quantity = 1m)
    {
        var open = await Client.PostAsJsonAsync("/api/resto/orders",
            new OpenRestoOrderRequest(RestoServiceType.PayAsYouOrder, setup.SessionId, setup.BranchId, null, "Counter"));
        open.EnsureSuccessStatusCode();
        var order = (await open.Content.ReadFromJsonAsync<RestoOrderDto>(TestJson.Options))!;
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity);
        await SettleAsync(order, setup, Guid.NewGuid());
        return await GetOrderAsync(order.Id);
    }

    protected async Task<RestoOrderDto> OpenOrderAsync(Setup setup, Guid? tableId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/resto/orders",
            new OpenRestoOrderRequest(RestoServiceType.BillOut, setup.SessionId, setup.BranchId, tableId ?? setup.TableId, "Table 1"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RestoOrderDto>(TestJson.Options))!;
    }

    protected async Task<RestoOrderDto> GetOrderAsync(Guid orderId)
    {
        var order = await Client.GetFromJsonAsync<RestoOrderDto>($"/api/resto/orders/{orderId}", TestJson.Options);
        return order!;
    }

    protected async Task<RestoOrderDto> AddRoundAsync(RestoOrderDto order)
    {
        var response = await Client.PostAsJsonAsync($"/api/resto/orders/{order.Id}/rounds",
            new RestoStructuralRequest(order.RowVersion));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RestoOrderDto>(TestJson.Options))!;
    }

    protected async Task<RestoOrderDto> AddItemAsync(RestoOrderDto order, Guid roundId, Setup setup, decimal quantity)
    {
        var response = await Client.PostAsJsonAsync($"/api/resto/orders/{order.Id}/items", new AddRestoItemRequest(
            order.RowVersion, roundId, setup.VariantId, quantity, setup.StationId,
            Array.Empty<Guid>(), Discount: null, Approval: null, KitchenNote: null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RestoOrderDto>(TestJson.Options))!;
    }

    protected async Task<RestoOrderDto> ReleaseRoundAsync(RestoOrderDto order, Guid roundId)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/rounds/{roundId}/release", new RestoStructuralRequest(order.RowVersion));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RestoOrderDto>(TestJson.Options))!;
    }

    protected static SettleRestoOrderRequest SettleBody(RestoOrderDto order, Setup setup, Guid settlementRequestId) =>
        new(order.RowVersion, settlementRequestId, setup.SessionId,
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) });

    protected async Task<RestoSettlementResultDto> SettleAsync(RestoOrderDto order, Setup setup, Guid settlementRequestId)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/settle", SettleBody(order, setup, settlementRequestId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RestoSettlementResultDto>(TestJson.Options))!;
    }

    /// <summary>Marks the item as acknowledged by the kitchen, so voiding it counts as consumed (waste).</summary>
    protected Task AcknowledgeItemAsync(Guid itemId, Guid userId) => InScopeAsync(async db =>
    {
        var item = await db.RestoOrderItems.SingleAsync(i => i.Id == itemId);
        item.Acknowledge(userId, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return true;
    });
}
