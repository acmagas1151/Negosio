using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

public class CloseVoidConcurrencyTests : IntegrationTest
{
    public CloseVoidConcurrencyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Concurrent_void_and_session_close_never_produce_an_inconsistent_reconciliation()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, openingStock: 10m);

        // From here on every call carries its own explicit bearer token via AuthorizedRequest, so the
        // ambient Client authorization set by RegisterLoginAndAuthorizeAsync above is no longer needed.
        Client.DefaultRequestHeaders.Authorization = null;

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkoutRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/pos/checkout", cashierToken,
            new CheckoutRequest(branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 1m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) })));
        var sale = (await checkoutRes.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        var voidTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/void", owner.AccessToken,
            new VoidSaleRequest("Concurrent with close")));
        var closeTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/register-sessions/{session.Id}/close", cashierToken,
            new CloseRegisterSessionRequest(500m)));

        await Task.WhenAll(voidTask, closeTask);
        var voidRes = await voidTask;
        var closeRes = await closeTask;

        var finalSaleRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/sales/{sale.SaleId}", owner.AccessToken));
        var finalSale = (await finalSaleRes.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;

        if (voidRes.IsSuccessStatusCode)
        {
            // Void won — the session must still have been Open when its transaction committed.
            finalSale.Sale.Status.Should().Be(SaleStatus.Voided);
            if (closeRes.IsSuccessStatusCode)
            {
                // Close ran after void released the lock — its own numbers must reflect the void,
                // not a stale pre-void snapshot.
                var closedSession = (await closeRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
                closedSession.VoidedCashSales.Should().Be(500m);
                closedSession.ExpectedCash.Should().Be(1000m); // opening only — the one sale was voided
            }
        }
        else
        {
            // Close won — void must have been rejected because the session was already closed by
            // the time void's own (serialized) check ran.
            finalSale.Sale.Status.Should().Be(SaleStatus.Completed);
            closeRes.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
