using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Platform;

public class TenantRoutingTests : IntegrationTest
{
    public TenantRoutingTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Writes_land_in_the_callers_own_database_only()
    {
        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "A"));
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "B Co", email: "b@example.com", branchCode: "B"));

        Authorize(tenantA.AccessToken);
        await CreateCategoryAsync("A-Only Category");

        await using (var aDb = await Factory.OpenTenantConnectionAsync(tenantA.User.TenantId))
        {
            (await PlatformSql.StringsAsync(aDb, "SELECT Name FROM Categories"))
                .Should().ContainSingle().Which.Should().Be("A-Only Category");
        }

        await using (var bDb = await Factory.OpenTenantConnectionAsync(tenantB.User.TenantId))
        {
            (await PlatformSql.CountAsync(bDb, "Categories")).Should().Be(0);
        }
    }

    [Fact]
    public async Task A_token_for_a_user_that_does_not_exist_is_rejected_with_401()
    {
        // No platform login for this subject -> the per-request account-state check fails the token
        // before tenant routing runs. (Phase 4: OnTokenValidated in ConfigureJwtBearerOptions.)
        var generator = Factory.Services.GetRequiredService<IJwtTokenGenerator>();
        var ghost = generator.Generate(new TokenSubject(Guid.NewGuid(), Guid.NewGuid(), UserRole.Owner, "ghost@example.com"));

        Authorize(ghost.Value);
        var response = await Client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_login_whose_tenant_route_is_missing_is_rejected_with_404()
    {
        // The account passes the state check but its tenant has no database mapping -> routing fails.
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        await InPlatformScopeAsync(async db =>
        {
            db.PlatformUserLogins.Add(Negosio.Domain.Entities.PlatformUserLogin.Create(
                userId, tenantId, "orphan@example.com", "not-a-real-hash", UserRole.Owner));
            await db.SaveChangesAsync();
            return true;
        });

        var generator = Factory.Services.GetRequiredService<IJwtTokenGenerator>();
        Authorize(generator.Generate(new TokenSubject(userId, tenantId, UserRole.Owner, "orphan@example.com")).Value);

        var response = await Client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("TENANT_NOT_FOUND");
    }

    [Fact]
    public async Task A_suspended_tenant_is_locked_out_with_403_and_no_tenant_db_is_touched()
    {
        var login = await RegisterAndLoginAsync(NewRegisterRequest(businessType: "Retail"));

        await InPlatformScopeAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync();
            tenant.Suspend();
            await db.SaveChangesAsync();
            return true;
        });

        // The routing cache holds a route for up to 60s; a real suspension takes effect once it
        // lapses. Evict it here so the test exercises the resolver's suspended-tenant path now.
        Factory.Services.GetRequiredService<IMemoryCache>().Remove($"tenant-route:{login.User.TenantId}");

        Authorize(login.AccessToken);
        var response = await Client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("TENANT_SUSPENDED");
    }
}
