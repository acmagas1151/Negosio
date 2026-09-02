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
    /// </summary>
    [Fact]
    public async Task Concurrent_cash_movement_and_session_close_never_silently_drop_the_movement()
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
        var closeTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/register-sessions/{session.Id}/close", cashierToken,
            new CloseRegisterSessionRequest(1000m)));

        await Task.WhenAll(movementTask, closeTask);
        var movementRes = await movementTask;
        var closeRes = await closeTask;

        // The close must always succeed here — nothing about a concurrent cash-movement attempt (won
        // or lost) makes a session unclosable.
        closeRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var closedSession = (await closeRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

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
}
