using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

/// <summary>
/// Regression coverage for the gap where <c>ReconcileAndCloseAsync</c> took its pessimistic lock but
/// never re-read the session's status afterward — only the stale, pre-transaction in-memory instance
/// was checked. Two concurrent closes of the same session (e.g. an Owner force-closing at the same
/// moment the cashier clicks Close) could both pass that stale guard and then both run
/// <c>session.Close(...)</c> one after another under the lock, the second silently overwriting the
/// first's ClosedByUserId/ClosingCash/CashDifference with no error — RegisterSession has no
/// RowVersion to catch that the way Sale does.
/// </summary>
public class SessionCloseConcurrencyTests : IntegrationTest
{
    public SessionCloseConcurrencyTests(NegosioApiFactory factory) : base(factory)
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
    /// Races the cashier's own close against an Owner force-close of the same session, with distinct
    /// closing-cash figures so the two outcomes are trivially distinguishable. Exactly one of the two
    /// requests must succeed; the other must be rejected with <see cref="ErrorCodes.RegisterSessionNotOpen"/>
    /// — never both succeeding (the silent-overwrite bug) and never both failing.
    /// <paramref name="closeHeadStart"/>, when given, delays starting the force-close request so the
    /// cashier's own close reaches the lock first — used to reliably exercise the "cashier's close
    /// wins" branch alongside the unbiased race's coverage of whichever branch it happens to resolve.
    /// </summary>
    private async Task RaceCloseAgainstForceCloseAsync(TimeSpan? closeHeadStart)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");

        Client.DefaultRequestHeaders.Authorization = null;

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var closeTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/register-sessions/{session.Id}/close", cashierToken,
            new CloseRegisterSessionRequest(1000m)));
        if (closeHeadStart is { } delay)
        {
            await Task.Delay(delay);
        }

        var forceCloseTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/register-sessions/{session.Id}/force-close", owner.AccessToken,
            new CloseRegisterSessionRequest(1200m)));

        await Task.WhenAll(closeTask, forceCloseTask);
        var closeRes = await closeTask;
        var forceCloseRes = await forceCloseTask;

        // Exactly one of the two must have succeeded — never both (the silent-overwrite bug this
        // test guards against) and never neither (the lock must never simply starve both callers).
        var succeeded = new[] { closeRes, forceCloseRes }.Count(r => r.StatusCode == HttpStatusCode.OK);
        succeeded.Should().Be(1, "exactly one of the two concurrent closes must win — never both, never neither");

        if (closeHeadStart is not null)
        {
            // The head start exists specifically to exercise the cashier-close-wins branch below — if
            // it ever stops winning, this test would silently degrade into a duplicate of the unbiased
            // race instead of testing what its name promises, so fail loudly instead.
            closeRes.StatusCode.Should().Be(HttpStatusCode.OK,
                "the cashier's own close was given a head start and should have reached the lock first");
        }

        if (closeRes.StatusCode == HttpStatusCode.OK)
        {
            // Cashier's close won — its own numbers (closingCash 1000, so no difference) must be the
            // ones that persisted; the force-close must have been rejected outright, never allowed to
            // silently overwrite them.
            var closedSession = (await closeRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
            closedSession.ClosingCash.Should().Be(1000m);
            closedSession.CashDifference.Should().Be(0m);

            forceCloseRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await forceCloseRes.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.RegisterSessionNotOpen);
        }
        else
        {
            // Force-close won — its own numbers (closingCash 1200, so +200 difference) must be the
            // ones that persisted; the cashier's own close must have been rejected outright.
            var closedSession = (await forceCloseRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
            closedSession.ClosingCash.Should().Be(1200m);
            closedSession.CashDifference.Should().Be(200m);

            closeRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await closeRes.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.RegisterSessionNotOpen);
        }
    }

    [Fact]
    public Task Concurrent_close_and_force_close_never_both_succeed() =>
        RaceCloseAgainstForceCloseAsync(closeHeadStart: null);

    /// <summary>
    /// Same race as above, but with the cashier's own close given a head start so it deterministically
    /// reaches the lock first — guaranteeing coverage of that branch regardless of whatever the
    /// unbiased race above happens to resolve to on a given run.
    /// </summary>
    [Fact]
    public Task Cashiers_close_given_a_head_start_over_force_close_still_never_both_succeed() =>
        RaceCloseAgainstForceCloseAsync(closeHeadStart: TimeSpan.FromMilliseconds(75));
}
