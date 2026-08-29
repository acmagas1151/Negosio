using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Pos;

public class RegisterSessionTests : IntegrationTest
{
    public RegisterSessionTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Owner_can_create_a_register_for_a_branch()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);

        var register = await CreateRegisterAsync(branchId, "Front Till", "r1");

        register.BranchId.Should().Be(branchId);
        register.Code.Should().Be("R1");
        register.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Cashier_can_open_and_close_a_session_with_reconciliation()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);

        var opened = await OpenSessionAsync(register.Id, openingCash: 500m);
        opened.Status.Should().Be(RegisterSessionStatus.Open);
        opened.OpeningCash.Should().Be(500m);

        var closeResponse = await Client.PostAsJsonAsync(
            $"/api/register-sessions/{opened.Id}/close",
            new CloseRegisterSessionRequest(500m));
        closeResponse.EnsureSuccessStatusCode();

        var closed = (await closeResponse.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
        closed.Status.Should().Be(RegisterSessionStatus.Closed);
        closed.ExpectedCash.Should().Be(500m);
        closed.CashDifference.Should().Be(0m);
    }

    [Fact]
    public async Task A_second_open_session_on_the_same_register_is_rejected()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        await OpenSessionAsync(register.Id);

        var response = await Client.PostAsJsonAsync(
            "/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 0m));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("REGISTER_SESSION_ALREADY_OPEN");
    }

    [Fact]
    public async Task Current_returns_the_open_session_for_a_register()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var opened = await OpenSessionAsync(register.Id);

        var current = await Client.GetFromJsonAsync<RegisterSessionDto>(
            $"/api/register-sessions/current?registerId={register.Id}", TestJson.Options);

        current!.Id.Should().Be(opened.Id);
    }

    [Fact]
    public async Task Tenant_A_cannot_open_a_session_on_tenant_B_register()
    {
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(email: "b@example.com", branchCode: "B"));
        Authorize(tenantB.AccessToken);
        var branchB = await GetMainBranchIdAsync(tenantB);
        var registerB = await CreateRegisterAsync(branchB);

        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        Authorize(tenantA.AccessToken);

        var response = await Client.PostAsJsonAsync(
            "/api/register-sessions/open",
            new OpenRegisterSessionRequest(registerB.Id, 0m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Closing_an_already_closed_session_is_rejected()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var opened = await OpenSessionAsync(register.Id);
        await Client.PostAsJsonAsync($"/api/register-sessions/{opened.Id}/close", new CloseRegisterSessionRequest(0m));

        var response = await Client.PostAsJsonAsync(
            $"/api/register-sessions/{opened.Id}/close", new CloseRegisterSessionRequest(0m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("REGISTER_SESSION_NOT_OPEN");
    }
}
