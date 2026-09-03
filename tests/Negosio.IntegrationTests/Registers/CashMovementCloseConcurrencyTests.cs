using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

public class CashMovementCloseConcurrencyTests : IntegrationTest
{
    public CashMovementCloseConcurrencyTests(NegosioApiFactory factory) : base(factory)
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
    /// Races a ₱300 cash-in against a close of the same session. Whichever wins the pessimistic lock
    /// must fully determine the other's outcome: the movement is never left inserted-but-unaccounted-for
    /// in the closed session's breakdown (a "phantom" cash movement with no trace in the reconciliation),
    /// and it is never counted in the breakdown without actually existing.
    /// <paramref name="movementHeadStart"/>, when given, delays starting the close request so the
    /// cash-movement request reaches the lock first — used to reliably exercise the movement-wins
    /// branch. Unlike the void-vs-close race (see <c>CloseVoidConcurrencyTests</c>), an unbiased race
    /// between these two on this machine is close to a coin flip (a 20-run diagnostic — removed before
    /// finalizing, per this codebase's established technique — measured roughly a 45/55 split), since
    /// their pre-lock work is nearly symmetric (each does one tracked session load plus an
    /// ownership/status check before opening its transaction and requesting the lock). Close is the
    /// slightly more frequent winner, so this head start still earns its keep: without it, a run of
    /// the unbiased test alone has no guarantee of ever exercising the movement-wins branch.
    /// </summary>
    private async Task RaceCashMovementAgainstCloseAsync(TimeSpan? movementHeadStart)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");

        Client.DefaultRequestHeaders.Authorization = null;

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var movementTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/register-sessions/{session.Id}/cash-movements", cashierToken,
            new CreateCashMovementRequest(CashMovementType.CashIn, 300m, "Concurrent with close")));
        if (movementHeadStart is { } delay)
        {
            await Task.Delay(delay);
        }

        var closeTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/register-sessions/{session.Id}/close", cashierToken,
            new CloseRegisterSessionRequest(1000m)));

        await Task.WhenAll(movementTask, closeTask);
        var movementRes = await movementTask;
        var closeRes = await closeTask;

        // The close must always succeed here — nothing about a concurrent cash-movement attempt (won
        // or lost) makes a session unclosable.
        closeRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var closedSession = (await closeRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        if (movementHeadStart is not null)
        {
            // The head start exists specifically to exercise the movement-wins branch below — if it
            // ever stops winning, this test would silently degrade into a duplicate of the unbiased
            // race instead of testing what its name promises, so fail loudly instead.
            movementRes.StatusCode.Should().Be(HttpStatusCode.Created,
                "the cash movement was given a head start and should have reached the lock first");
        }

        if (movementRes.StatusCode == HttpStatusCode.Created)
        {
            // Movement won the lock and committed before close's SUM query ran (or close hadn't yet
            // reached the lock) — its ₱300 must be genuinely reflected in the closed breakdown.
            closedSession.CashIn.Should().Be(300m);
            closedSession.ExpectedCash.Should().Be(1300m); // opening 1000 + the ₱300 cash-in
        }
        else
        {
            // Close won the lock — the movement's own authoritative re-check must have observed the
            // session already Closed and rejected it outright, never inserted it unaccounted-for.
            movementRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await movementRes.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.CashMovementSessionClosed);
            closedSession.CashIn.Should().Be(0m);
            closedSession.ExpectedCash.Should().Be(1000m); // opening only — the movement never landed
        }
    }

    [Fact]
    public Task Concurrent_cash_movement_and_session_close_never_silently_drop_the_movement() =>
        RaceCashMovementAgainstCloseAsync(movementHeadStart: null);

    /// <summary>
    /// Same race as above, but with the cash movement given a head start so it deterministically
    /// reaches the lock first — guaranteeing coverage of the movement-wins branch on every run, rather
    /// than leaving it to the roughly-even odds the unbiased race resolves it by (see
    /// <see cref="RaceCashMovementAgainstCloseAsync"/>'s doc comment).
    /// </summary>
    [Fact]
    public Task Cash_movement_given_a_head_start_over_close_still_never_drops_the_movement() =>
        RaceCashMovementAgainstCloseAsync(movementHeadStart: TimeSpan.FromMilliseconds(75));
}
