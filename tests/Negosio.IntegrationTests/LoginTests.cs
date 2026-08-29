using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Auth;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests;

public class LoginTests : IntegrationTest
{
    public LoginTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Valid_credentials_return_token_and_profile()
    {
        await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest());

        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("OWNER@example.com", "SecurePassword123!"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(TestJson.Options);
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
        body.User.Email.Should().Be("owner@example.com");
        body.User.Role.Should().Be(Negosio.Domain.Enums.UserRole.Owner);
        body.User.TenantName.Should().Be("Bruno's Cafe");
    }

    [Fact]
    public async Task Jwt_contains_tenant_id_claim()
    {
        var login = await RegisterAndLoginAsync();

        var token = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);

        token.Claims.Should().Contain(c => c.Type == "tenant_id" && c.Value == login.User.TenantId.ToString());
        token.Claims.Should().Contain(c => c.Type == "sub" && c.Value == login.User.Id.ToString());
        token.Claims.Should().Contain(c => c.Type == "role" && c.Value == "Owner");
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest());

        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("owner@example.com", "WrongPassword123!"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Unknown_email_is_rejected()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("nobody@example.com", "SecurePassword123!"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("INVALID_CREDENTIALS");
    }
}
