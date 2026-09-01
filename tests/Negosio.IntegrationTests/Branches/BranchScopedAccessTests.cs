using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Auth;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Branches;

public class BranchScopedAccessTests : IntegrationTest
{
    public BranchScopedAccessTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record TwoBranchWorld(
        LoginResponse Owner, Guid MainId, Guid BgcId,
        Guid MainProductId, Guid MainVariantId, Guid BgcVariantId,
        Guid MainSessionId, Guid MainSaleId, string BgcCashierToken);

    /// <summary>MAIN + BGC, each with a stocked product; one sale rung up at MAIN; a BGC-bound Cashier.</summary>
    private async Task<TwoBranchWorld> ArrangeAsync()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");

        var category = await CreateCategoryAsync();
        var (mainProductId, mainVariantId) = await SeedStockedProductAsync(mainId, category.Id, "Widget", "W-MAIN", openingStock: 10m);
        var (_, bgcVariantId) = await SeedStockedProductAsync(bgc.Id, category.Id, "Gadget", "G-BGC", openingStock: 3m);

        var register = await CreateRegisterAsync(mainId, "Main R1", "MR1");
        var session = await OpenSessionAsync(register.Id);
        var sale = await CheckoutOkAsync(new CheckoutRequest(
            mainId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(mainVariantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);
        return new TwoBranchWorld(owner, mainId, bgc.Id, mainProductId, mainVariantId, bgcVariantId, session.Id, sale.SaleId, cashierToken);
    }

    [Fact]
    public async Task Inventory_list_is_forced_to_the_assigned_branch()
    {
        var w = await ArrangeAsync();
        Authorize(w.BgcCashierToken);

        var page = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>("/api/inventory?pageSize=50", TestJson.Options);
        page!.Items.Should().OnlyContain(r => r.BranchId == w.BgcId);

        // even explicitly asking for MAIN
        var forced = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>($"/api/inventory?branchId={w.MainId}&pageSize=50", TestJson.Options);
        forced!.Items.Should().OnlyContain(r => r.BranchId == w.BgcId);
    }

    [Fact]
    public async Task Adjusting_stock_in_a_foreign_branch_is_forbidden()
    {
        var w = await ArrangeAsync();
        // InventoryStaff has InventoryWrite but is branch-scoped — the resolver (not the policy) rejects the foreign branch.
        Authorize(w.Owner.AccessToken);
        var invToken = await AddTenantUserTokenAsync("inv@example.com", UserRole.InventoryStaff, w.BgcId);
        Authorize(invToken);

        var response = await Client.PostAsJsonAsync("/api/inventory/adjustments", new AdjustInventoryRequest(
            w.MainId, w.MainProductId, null, 5m, "hax", ReorderLevel: null, ExpectedConcurrencyToken: null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("BRANCH_FORBIDDEN");
    }

    [Fact]
    public async Task Sales_are_scoped_and_a_foreign_sale_is_a_404()
    {
        var w = await ArrangeAsync();
        Authorize(w.BgcCashierToken);

        var list = await Client.GetFromJsonAsync<PagedResult<SaleSummaryDto>>("/api/sales", TestJson.Options);
        list!.Items.Should().NotContain(s => s.Id == w.MainSaleId);

        (await Client.GetAsync($"/api/sales/{w.MainSaleId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.GetAsync($"/api/sales/{w.MainSaleId}/receipt")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Checkout_and_catalog_reject_a_foreign_branch()
    {
        var w = await ArrangeAsync();
        Authorize(w.BgcCashierToken);

        (await Client.GetAsync($"/api/pos/catalog?branchId={w.MainId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var checkout = await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            w.MainId, w.MainSessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(w.MainVariantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));
        checkout.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_sees_every_branch()
    {
        var w = await ArrangeAsync();
        Authorize(w.Owner.AccessToken);

        var page = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>("/api/inventory?pageSize=50", TestJson.Options);
        page!.Items.Select(r => r.BranchId).Distinct().Should().BeEquivalentTo(new[] { w.MainId, w.BgcId });
    }

    [Fact]
    public async Task Selling_at_one_branch_leaves_the_other_branchs_stock_untouched()
    {
        var w = await ArrangeAsync(); // MAIN opening 10, minus 1 sold = 9; BGC opening 3
        Authorize(w.Owner.AccessToken);

        var main = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>($"/api/inventory?branchId={w.MainId}&pageSize=50", TestJson.Options);
        var bgc = await Client.GetFromJsonAsync<PagedResult<InventoryRowDto>>($"/api/inventory?branchId={w.BgcId}&pageSize=50", TestJson.Options);

        main!.Items.Single(r => r.ProductVariantId == w.MainVariantId).QuantityOnHand.Should().Be(9m);
        bgc!.Items.Single(r => r.ProductVariantId == w.BgcVariantId).QuantityOnHand.Should().Be(3m);
    }

    [Fact]
    public async Task Each_branch_runs_its_own_sale_number_sequence()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var category = await CreateCategoryAsync();
        var (_, mainVariant) = await SeedStockedProductAsync(mainId, category.Id, "Widget", "W1", openingStock: 20m);
        var (_, bgcVariant) = await SeedStockedProductAsync(bgc.Id, category.Id, "Gadget", "G1", openingStock: 20m);

        var mainReg = await CreateRegisterAsync(mainId, "MR", "MR");
        var bgcReg = await CreateRegisterAsync(bgc.Id, "BR", "BR");
        var mainSession = await OpenSessionAsync(mainReg.Id);
        var bgcSession = await OpenSessionAsync(bgcReg.Id);

        async Task<string> Sell(Guid branch, Guid session, Guid variant)
        {
            var r = await CheckoutOkAsync(new CheckoutRequest(
                branch, session, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variant, 1m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));
            return r.SaleNumber;
        }

        (await Sell(mainId, mainSession.Id, mainVariant)).Should().Be("00000001");
        (await Sell(bgc.Id, bgcSession.Id, bgcVariant)).Should().Be("00000001");
        (await Sell(mainId, mainSession.Id, mainVariant)).Should().Be("00000002");
        (await Sell(bgc.Id, bgcSession.Id, bgcVariant)).Should().Be("00000002");
    }

    [Fact]
    public async Task A_branch_scoped_user_only_sees_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, bgc.Id);

        Authorize(cashierToken);
        var scoped = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches?includeInactive=true", TestJson.Options);
        scoped.Should().ContainSingle().Which.Id.Should().Be(bgc.Id);

        Authorize(owner.AccessToken);
        var all = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches?includeInactive=true", TestJson.Options);
        all.Should().Contain(b => b.Id == mainId).And.Contain(b => b.Id == bgc.Id);
    }

    [Fact]
    public async Task An_unassigned_branch_scoped_user_is_forbidden()
    {
        await RegisterLoginAndAuthorizeAsync();
        var token = await AddTenantUserTokenAsync("noassign@example.com", UserRole.Cashier);

        Authorize(token);
        var response = await Client.GetAsync("/api/branches");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("BRANCH_FORBIDDEN");
    }
}
