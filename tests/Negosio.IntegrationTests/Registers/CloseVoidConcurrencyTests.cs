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

    /// <summary>
    /// Sets up a session with one ₱500 cash sale, then races a void of that sale against a close of
    /// its session, asserting the outcome is consistent regardless of which one wins the pessimistic
    /// lock. <paramref name="voidHeadStart"/>, when given, delays starting the close request so void
    /// gets there first — used to reliably exercise the void-wins branch, since void does noticeably
    /// more pre-transaction work (a tracked sale load with Include, a branch-access resolution, and
    /// EnsureEligibleAsync's two queries) than close's single session load before either even opens
    /// its transaction and requests the lock, so an unbiased race resolves in close's favor almost
    /// every time on this machine.
    /// </summary>
    private async Task RaceVoidAgainstCloseAsync(TimeSpan? voidHeadStart)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        // sellingPrice must actually be 500 (not the 75 default) — checkout applies cash payments as
        // min(received, grandTotal), so a mismatched default price here would silently record a Payment
        // of 75, not 500, and desync every assertion below from what the sale/void actually produced.
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 500m, openingStock: 10m);

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
        if (voidHeadStart is { } delay)
        {
            await Task.Delay(delay);
        }

        var closeTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/register-sessions/{session.Id}/close", cashierToken,
            new CloseRegisterSessionRequest(500m)));

        await Task.WhenAll(voidTask, closeTask);
        var voidRes = await voidTask;
        var closeRes = await closeTask;

        var finalSaleRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/sales/{sale.SaleId}", owner.AccessToken));
        var finalSale = (await finalSaleRes.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;

        if (voidHeadStart is not null)
        {
            // The head start exists specifically to exercise the void-wins branch below — if it ever
            // stops winning, this test would silently degrade into a duplicate of the unbiased race
            // instead of testing what its name promises, so fail loudly instead.
            voidRes.IsSuccessStatusCode.Should().BeTrue("void was given a head start and should have reached the lock first");
        }

        if (voidRes.IsSuccessStatusCode)
        {
            // Void won — the session must still have been Open when its transaction committed, so
            // close (running after void released the lock) must succeed unconditionally here — under
            // the intended design there is no legitimate way for close to fail in this branch.
            finalSale.Sale.Status.Should().Be(SaleStatus.Voided);
            closeRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // Close's own numbers must reflect the void, not a stale pre-void snapshot.
            var closedSession = (await closeRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
            closedSession.VoidedCashSales.Should().Be(500m);
            closedSession.ExpectedCash.Should().Be(1000m); // opening only — the one sale was voided
        }
        else
        {
            // Close won — void must have been rejected because the session was already closed by
            // the time void's own (serialized) check ran.
            finalSale.Sale.Status.Should().Be(SaleStatus.Completed);
            closeRes.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public Task Concurrent_void_and_session_close_never_produce_an_inconsistent_reconciliation() =>
        RaceVoidAgainstCloseAsync(voidHeadStart: null);

    /// <summary>
    /// Same race as above, but with void given a head start so it reliably reaches the lock first —
    /// giving real coverage of the void-wins branch, which an unbiased race essentially never exercises
    /// on this machine (see <see cref="RaceVoidAgainstCloseAsync"/>'s doc comment).
    /// </summary>
    [Fact]
    public Task Void_given_a_head_start_over_close_still_never_produces_an_inconsistent_reconciliation() =>
        RaceVoidAgainstCloseAsync(voidHeadStart: TimeSpan.FromMilliseconds(75));
}
