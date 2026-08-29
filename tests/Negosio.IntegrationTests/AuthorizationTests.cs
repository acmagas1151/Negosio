using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests;

public class AuthorizationTests : IntegrationTest
{
    public AuthorizationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Me_requires_authentication()
    {
        var response = await Client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task Me_returns_the_authenticated_users_tenant()
    {
        var login = await RegisterAndLoginAsync();
        Authorize(login.AccessToken);

        var response = await Client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<Negosio.Application.Auth.AuthUserDto>(TestJson.Options);
        me!.TenantId.Should().Be(login.User.TenantId);
        me.TenantName.Should().Be("Bruno's Cafe");
        me.Email.Should().Be("owner@example.com");
    }

    [Fact]
    public async Task Dashboard_requires_authentication()
    {
        var response = await Client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dashboard_returns_tenant_scoped_counts()
    {
        var login = await RegisterAndLoginAsync();
        Authorize(login.AccessToken);

        var response = await Client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dashboard = await response.Content.ReadFromJsonAsync<Negosio.Application.Dashboard.DashboardResponse>(TestJson.Options);
        dashboard!.Tenant.Id.Should().Be(login.User.TenantId);
        dashboard.BranchCount.Should().Be(1);
        dashboard.UserCount.Should().Be(1);
    }

    [Fact]
    public async Task Owner_only_endpoint_allows_owner()
    {
        var login = await RegisterAndLoginAsync();
        Authorize(login.AccessToken);

        var response = await Client.GetAsync("/api/admin/owner-test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Owner_only_endpoint_forbids_non_owner()
    {
        var login = await RegisterAndLoginAsync();

        // Add a Viewer to the same tenant and mint a token for them.
        var viewerToken = await InScopeAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == login.User.TenantId);
            var viewer = tenant.AddUser("viewer@example.com", "x", "View", "Er", UserRole.Viewer);
            await db.SaveChangesAsync();

            var generator = Factory.Services.GetRequiredService<IJwtTokenGenerator>();
            return generator.Generate(viewer).Value;
        });

        Authorize(viewerToken);
        var response = await Client.GetAsync("/api/admin/owner-test");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Malformed_token_is_rejected()
    {
        Authorize("not-a-real-token");

        var response = await Client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tampered_token_is_rejected()
    {
        var login = await RegisterAndLoginAsync();
        Authorize(login.AccessToken + "tampered");

        var response = await Client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
