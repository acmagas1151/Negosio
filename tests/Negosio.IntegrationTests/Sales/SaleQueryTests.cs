using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class SaleQueryTests : IntegrationTest
{
    public SaleQueryTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<(Guid BranchId, Guid SessionId, Guid VariantId)> ArrangeAsync()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 40m, openingStock: 50m);
        return (branchId, session.Id, variantId);
    }

    private Task<SaleResultDto> SellAsync(Guid branchId, Guid sessionId, Guid variantId, decimal qty = 1m) =>
        CheckoutOkAsync(new CheckoutRequest(
            branchId, sessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) }));

    [Fact]
    public async Task List_filters_by_status_and_searches_by_sale_number_and_is_tenant_scoped()
    {
        var (branchId, sessionId, variantId) = await ArrangeAsync();
        var a = await SellAsync(branchId, sessionId, variantId);
        await SellAsync(branchId, sessionId, variantId);

        var all = await Client.GetFromJsonAsync<PagedResult<SaleSummaryDto>>("/api/sales", TestJson.Options);
        all!.TotalCount.Should().Be(2);

        var byNumber = await Client.GetFromJsonAsync<PagedResult<SaleSummaryDto>>(
            $"/api/sales?search={a.SaleNumber}", TestJson.Options);
        byNumber!.Items.Should().ContainSingle(s => s.SaleNumber == a.SaleNumber);

        var completed = await Client.GetFromJsonAsync<PagedResult<SaleSummaryDto>>(
            "/api/sales?status=Completed&pageSize=1", TestJson.Options);
        completed!.TotalCount.Should().Be(2);
        completed.Items.Should().HaveCount(1);

        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);
        var isolated = await Client.GetFromJsonAsync<PagedResult<SaleSummaryDto>>("/api/sales", TestJson.Options);
        isolated!.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Cashier_does_not_see_cost_snapshot_in_sale_detail()
    {
        var (branchId, sessionId, variantId) = await ArrangeAsync();
        var sale = await SellAsync(branchId, sessionId, variantId);

        var ownerView = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);
        ownerView!.Items.Single().CostPriceSnapshot.Should().NotBeNull();

        var cashierToken = await InScopeAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync();
            var cashier = tenant.AddUser("cashier@example.com", "x", "Cash", "Ier", UserRole.Cashier);
            await db.SaveChangesAsync();
            return Factory.Services.GetRequiredService<IJwtTokenGenerator>().Generate(cashier).Value;
        });

        Authorize(cashierToken);
        var cashierView = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);
        cashierView!.Items.Single().CostPriceSnapshot.Should().BeNull();
    }
}
