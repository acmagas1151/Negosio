using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class VoidConcurrencyTests : IntegrationTest
{
    public VoidConcurrencyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    // Explicit per-request Authorization header, NOT the shared Client.DefaultRequestHeaders the
    // Authorize(token) helper mutates — two concurrent requests as different actors must not race
    // on that shared mutable state. Clear Client.DefaultRequestHeaders.Authorization = null before
    // using this in a test (verify against the real Authorize() implementation first).
    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Two_concurrent_voids_on_the_same_sale_never_double_restore_inventory()
    {
        // RegisterLoginAndAuthorizeAsync's own Authorize() call sets Client.DefaultRequestHeaders —
        // deliberately left in place (not cleared) for the shared-client setup helpers below
        // (CreateRegisterAsync, CreateCategoryAsync, SeedStockedProductAsync, GetInventoryQuantityAsync)
        // that read the current auth from Client.DefaultRequestHeaders rather than taking a token
        // parameter. It is harmless for the concurrent requests themselves: HttpClient always prefers
        // a header already present on the HttpRequestMessage over the same header in
        // DefaultRequestHeaders, so every AuthorizedRequest(...) call below still sends exactly the
        // token it was built with, never the client's default.
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, openingStock: 10m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkoutRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/pos/checkout", cashierToken,
            new CheckoutRequest(branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 3m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 300m) })));
        var sale = (await checkoutRes.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        var beforeVoid = await GetInventoryQuantityAsync(branchId, variantId);

        HttpRequestMessage VoidRequest() => AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/void", owner.AccessToken,
            new VoidSaleRequest("Concurrent void attempt"));

        var results = await Task.WhenAll(Client.SendAsync(VoidRequest()), Client.SendAsync(VoidRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(beforeVoid + 3m); // restored exactly once
    }

    [Fact]
    public async Task Concurrent_void_and_return_on_the_same_sale_never_both_succeed()
    {
        // See the comment in the previous test — Client.DefaultRequestHeaders is deliberately left
        // set to the owner's token; it's needed by the shared-client setup helpers below and is
        // harmless for every AuthorizedRequest(...) call, which always sends its own explicit token.
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, openingStock: 10m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkoutRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/pos/checkout", cashierToken,
            new CheckoutRequest(branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 2m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) })));
        var sale = (await checkoutRes.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        var detailRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/sales/{sale.SaleId}", owner.AccessToken));
        var detail = (await detailRes.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        var firstItemId = detail.Items[0].Id;

        var voidTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/void", owner.AccessToken,
            new VoidSaleRequest("Concurrent void")));
        var returnTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/returns", owner.AccessToken,
            new CreateReturnRequest(new[] { new ReturnLineInput(firstItemId, 1m, true) }, "Concurrent return", PaymentMethod.Cash, null)));

        var results = await Task.WhenAll(voidTask, returnTask);

        // Exactly one of the two can succeed — a sale can never end up both Voided and Refunded.
        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);

        var finalRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/sales/{sale.SaleId}", owner.AccessToken));
        var final = (await finalRes.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        final.Sale.Status.Should().BeOneOf(SaleStatus.Voided, SaleStatus.PartiallyRefunded, SaleStatus.Refunded);
    }
}
