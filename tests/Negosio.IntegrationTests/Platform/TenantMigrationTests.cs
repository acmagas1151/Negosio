using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
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

    /// <summary>
    /// Spec item 37 ("Existing data migrates correctly") and the Migration section's explicit
    /// requirements: preserve existing Sales, preserve DeliveryRequiredQuantity, preserve
    /// DeliveryReceipt/DeliveryReceiptItem history, default existing rows to zero Pickup allocation,
    /// create no false Pending Pickup backlog, and keep existing Delivered records meaning what they
    /// meant. The two prior tests only prove a FRESH tenant applies every migration cleanly; neither
    /// ever puts pre-Pickup-plan data through the actual Up() of the Pickup migrations. This test does:
    /// it creates a real Sale/SaleItem/completed-Delivery through today's fully-migrated API (this is
    /// what "existing data" concretely looks like), rolls the tenant schema back to the migration
    /// immediately before the two Pickup-plan migrations existed — which drops
    /// SaleItems.PickupRequiredQuantity, drops DeliveryReceipts.Method/CancellationDisposition, drops
    /// FulfillmentConversions, and renames the DeliveryReceipts columns back to their pre-plan names —
    /// then re-applies forward to latest, exactly as a real deployment would against a real tenant
    /// database. The rows created before the round trip are the "existing data"; surviving intact is
    /// the thing being verified.
    /// </summary>
    [Fact]
    public async Task Existing_pre_pickup_delivery_data_survives_the_pickup_migration_forward_and_back()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var tenantId = login.User.TenantId;
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1500m) },
            Method: FulfillmentMethod.Delivery));
        var saleItemId = sale.Items[0].SaleItemId;

        var created = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new Negosio.Application.Delivery.CreateDeliveryReceiptRequest(
                DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)), "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null));
        created.EnsureSuccessStatusCode();
        var dr = (await created.Content.ReadFromJsonAsync<Negosio.Application.Delivery.FulfillmentScheduleDto>(TestJson.Options))!;
        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

        await using (var db = await factory.CreateAsync(tenantId))
        {
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260912042018_MakeBatchRequestIdIndexNonUnique");
        }

        await using (var db = await factory.CreateAsync(tenantId))
        {
            // Confirms the rollback actually took the two Pickup-plan migrations back out.
            (await db.Database.GetPendingMigrationsAsync()).Should().HaveCount(2);

            await db.Database.MigrateAsync(); // forward again to latest — what a real deploy does
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        await InTenantScopeAsync(tenantId, async db =>
        {
            var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(i => i.Id == saleItemId);
            saleItem.PickupRequiredQuantity.Should().Be(0m); // new column defaults to zero, not a guess
            saleItem.DeliveryRequiredQuantity.Should().Be(10m); // untouched by the round trip — whole-sale intent
            saleItem.TakeNowQuantity.Should().Be(0m);

            var receipt = await db.DeliveryReceipts.AsNoTracking().SingleAsync(d => d.Id == dr.Id);
            receipt.Method.Should().Be(FulfillmentMethod.Delivery); // correctly defaulted for a pre-existing row
            receipt.CancellationDisposition.Should().BeNull();
            receipt.Status.Should().Be(FulfillmentStatus.Completed); // "Delivered" survives the column renames
            receipt.CompletedAtUtc.Should().NotBeNull();
            (await db.DeliveryReceiptItems.CountAsync(i => i.DeliveryReceiptId == dr.Id)).Should().Be(1);

            // No false Pending Pickup backlog was fabricated for this pre-existing sale.
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == sale.SaleId && d.Method == FulfillmentMethod.Pickup))
                .Should().Be(0);
            (await db.FulfillmentConversions.CountAsync(c => c.SaleId == sale.SaleId)).Should().Be(0);
            return true;
        });
    }
}
