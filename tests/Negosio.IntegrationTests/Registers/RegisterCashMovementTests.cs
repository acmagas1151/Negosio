using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

public class RegisterCashMovementTests : IntegrationTest
{
    public RegisterCashMovementTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Cashier_can_cash_in_and_out_on_their_own_open_session()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);

        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 1000m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var cashIn = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 500m, "Additional float"));
        cashIn.StatusCode.Should().Be(HttpStatusCode.Created);

        var cashOut = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashOut, 200m, "Petty cash"));
        cashOut.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await Client.GetFromJsonAsync<List<RegisterCashMovementDto>>(
            $"/api/register-sessions/{session.Id}/cash-movements", TestJson.Options);
        list!.Should().HaveCount(2);
    }

    [Fact]
    public async Task Amount_must_be_greater_than_zero()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);

        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 0m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var res = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 0m, "Bad amount"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cannot_create_a_movement_on_another_users_session()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierAToken = await AddTenantUserTokenAsync("a@example.com", UserRole.Cashier, branchId);
        var cashierBToken = await AddTenantUserTokenAsync("b@example.com", UserRole.Cashier, branchId);

        Authorize(cashierAToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 500m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        Authorize(cashierBToken);
        var res = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 100m, "Not mine"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.CashMovementNotOwner);
    }

    [Fact]
    public async Task Cannot_create_a_movement_on_a_closed_session()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);

        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 500m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
        await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/close", new CloseRegisterSessionRequest(500m));

        var res = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 100m, "Too late"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.CashMovementSessionClosed);
    }
}
