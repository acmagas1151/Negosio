using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Platform;

public class TenantMigrationTests : IntegrationTest
{
    public TenantMigrationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task A_freshly_provisioned_tenant_database_is_fully_migrated()
    {
        var body = (await (await Client.PostAsJsonAsync("/api/auth/register",
            NewRegisterRequest(businessType: "Retail"))).Content.ReadFromJsonAsync<RegisterResponseBody>())!;

        await InTenantScopeAsync(body.TenantId, async db =>
        {
            var pending = await db.Database.GetPendingMigrationsAsync();
            pending.Should().BeEmpty();

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Contain(m => m.EndsWith("_TenantBaseline"));
            return true;
        });
    }

    [Fact]
    public async Task Re_running_the_tenant_migrator_is_a_no_op()
    {
        var body = (await (await Client.PostAsJsonAsync("/api/auth/register",
            NewRegisterRequest(businessType: "Retail"))).Content.ReadFromJsonAsync<RegisterResponseBody>())!;

        using var scope = Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

        await using var db = await factory.CreateAsync(body.TenantId);
        // MigrateAsync is what provisioning runs; a second pass must not throw or change anything.
        await db.Database.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
