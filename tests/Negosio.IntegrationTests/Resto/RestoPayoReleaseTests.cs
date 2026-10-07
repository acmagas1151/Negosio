using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Negosio.Api.Hosting;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Application.Resto;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Resto;

/// <summary>
/// Pay-as-you-order release: the background worker, the manual action, their races, sale eligibility, tenant isolation,
/// and the pending and unacknowledged lists. The worker runs with a zero grace period in tests (see NegosioApiFactory).
/// </summary>
public class RestoPayoReleaseTests : RestoTestBase
{
    public RestoPayoReleaseTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Worker_releases_a_due_round_once_and_a_later_cycle_is_a_no_op_with_no_user_actor()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var order = await SettlePayoOrderAsync(setup);
        var roundId = order.Rounds.Single().Id;

        Factory.Services.GetRequiredService<RestoReconciliationOptions>().ReleaseGraceSeconds.Should().Be(0);
        var first = await RunWorkerAsync(setup.TenantId);
        first.Released.Should().Be(1);

        var second = await RunWorkerAsync(setup.TenantId);
        second.Released.Should().Be(0);
        second.Scanned.Should().Be(0, "a released round is no longer due");

        var round = await GetRoundAsync(setup.TenantId, roundId);
        round.Status.Should().Be(RestoOrderRoundStatus.Released);
        round.ReleasedByUserId.Should().BeNull("a system release records no user actor");
    }

    [Fact]
    public async Task Concurrent_worker_attempts_release_the_same_round_exactly_once()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var order = await SettlePayoOrderAsync(setup);
        var roundId = order.Rounds.Single().Id;

        var cycles = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RunWorkerAsync(setup.TenantId)));

        cycles.Sum(c => c.Released).Should().Be(1, "exactly one attempt wins the order lock");

        var round = await GetRoundAsync(setup.TenantId, roundId);
        round.Status.Should().Be(RestoOrderRoundStatus.Released);
        var releasedAt = round.ReleasedAtUtc;
        releasedAt.Should().NotBeNull();

        // A later attempt must not move the release timestamp.
        await RunWorkerAsync(setup.TenantId);
        (await GetRoundAsync(setup.TenantId, roundId)).ReleasedAtUtc.Should().Be(releasedAt);
    }

    [Fact]
    public async Task A_manual_release_with_a_stale_version_after_the_worker_is_a_clean_conflict()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var order = await SettlePayoOrderAsync(setup);
        var roundId = order.Rounds.Single().Id;

        // The client read the order before the worker released it.
        var staleVersion = order.RowVersion;
        await RunWorkerAsync(setup.TenantId);

        var response = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/rounds/{roundId}/release", new RestoStructuralRequest(staleVersion));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.RestoOrderConcurrencyConflict);
    }

    [Fact]
    public async Task A_manual_release_first_makes_the_worker_a_no_op_and_keeps_the_releaser()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var order = await SettlePayoOrderAsync(setup);
        var roundId = order.Rounds.Single().Id;

        var response = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/rounds/{roundId}/release", new RestoStructuralRequest(order.RowVersion));
        response.EnsureSuccessStatusCode();

        var cycle = await RunWorkerAsync(setup.TenantId);
        cycle.Scanned.Should().Be(0);
        cycle.Released.Should().Be(0);

        var ownerId = await GetUserIdFromTokenAsync(setup.OwnerToken);
        var round = await GetRoundAsync(setup.TenantId, roundId);
        round.ReleasedByUserId.Should().Be(ownerId);
    }

    [Fact]
    public async Task A_settled_order_whose_sale_was_voided_is_never_released()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var order = await SettlePayoOrderAsync(setup);
        var roundId = order.Rounds.Single().Id;

        var voided = await Client.PostAsJsonAsync($"/api/sales/{order.SaleId}/void", new VoidSaleRequest("Customer cancelled"));
        voided.EnsureSuccessStatusCode();

        var cycle = await RunWorkerAsync(setup.TenantId);
        cycle.Released.Should().Be(0);
        cycle.Refused.Should().Be(1);
        (await GetRoundAsync(setup.TenantId, roundId)).Status.Should().Be(RestoOrderRoundStatus.Draft);

        var fresh = await GetOrderAsync(order.Id);
        var manual = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/rounds/{roundId}/release", new RestoStructuralRequest(fresh.RowVersion));
        manual.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await manual.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.RestoReleaseSaleNotCompleted);
    }

    [Fact]
    public async Task A_worker_cycle_for_one_tenant_releases_only_that_tenants_rounds()
    {
        var tenantA = await SetupPayoAsync(openingStock: 10m);
        var orderA = await SettlePayoOrderAsync(tenantA);

        var tenantB = await SetupPayoAsync(openingStock: 10m, NewRegisterRequest(
            businessName: "Other Shop", branchCode: "OTH", email: "owner.b@example.com"));
        var orderB = await SettlePayoOrderAsync(tenantB);

        var cycle = await RunWorkerAsync(tenantA.TenantId);
        cycle.Released.Should().Be(1);

        (await GetRoundAsync(tenantA.TenantId, orderA.Rounds.Single().Id)).Status.Should().Be(RestoOrderRoundStatus.Released);
        (await GetRoundAsync(tenantB.TenantId, orderB.Rounds.Single().Id)).Status.Should().Be(RestoOrderRoundStatus.Draft);

        // Tenant B's pending list shows only its own order.
        Authorize(tenantB.OwnerToken);
        var page = await Client.GetFromJsonAsync<RestoKeysetPageDto<PendingPayoReleaseRowDto>>(
            "/api/resto/orders/pending-releases", TestJson.Options);
        page!.Items.Select(r => r.OrderId).Should().Equal(orderB.Id);
    }

    [Fact]
    public async Task A_failing_tenant_does_not_stop_a_healthy_tenant_in_the_same_worker_cycle()
    {
        var healthy = await SetupPayoAsync(openingStock: 10m);
        var healthyOrder = await SettlePayoOrderAsync(healthy);

        var broken = await SetupPayoAsync(openingStock: 10m, NewRegisterRequest(
            businessName: "Broken Shop", branchCode: "BRK", email: "owner.broken@example.com"));
        await SettlePayoOrderAsync(broken);

        var originalName = await SetDatabaseNameAsync(broken.TenantId, "Negosio.DoesNotExist_" + Guid.NewGuid().ToString("N"));
        try
        {
            var worker = new PayoReleaseWorker(
                Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                new RestoReconciliationOptions { PollIntervalSeconds = 10, ReleaseGraceSeconds = 0 },
                TimeProvider.System,
                NullLogger<PayoReleaseWorker>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await worker.StartAsync(cts.Token);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline
                       && (await GetRoundAsync(healthy.TenantId, healthyOrder.Rounds.Single().Id)).Status != RestoOrderRoundStatus.Released)
                {
                    await Task.Delay(300);
                }
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }

            (await GetRoundAsync(healthy.TenantId, healthyOrder.Rounds.Single().Id)).Status.Should().Be(RestoOrderRoundStatus.Released);
        }
        finally
        {
            await SetDatabaseNameAsync(broken.TenantId, originalName);
        }
    }

    [Fact]
    public async Task An_order_in_backoff_is_skipped_and_does_not_hold_back_later_orders()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var failing = await SettlePayoOrderAsync(setup);
        var healthy = await SettlePayoOrderAsync(setup);

        // Settled first, so without the backoff it would sit at the head of every batch.
        using (var scope = Factory.Services.CreateScope())
        {
            var backoff = scope.ServiceProvider.GetRequiredService<FailureBackoff<(Guid TenantId, Guid OrderId)>>();
            backoff.RecordFailure((setup.TenantId, failing.Id), DateTime.UtcNow, maxMinutes: 10);
        }

        var cycle = await RunWorkerAsync(setup.TenantId);

        cycle.Released.Should().Be(1);
        (await GetRoundAsync(setup.TenantId, healthy.Rounds.Single().Id)).Status.Should().Be(RestoOrderRoundStatus.Released);
        (await GetRoundAsync(setup.TenantId, failing.Rounds.Single().Id)).Status.Should().Be(RestoOrderRoundStatus.Draft);
    }

    [Fact]
    public async Task Pending_releases_page_through_every_row_exactly_once()
    {
        var setup = await SetupPayoAsync(openingStock: 30m);
        var orders = new List<RestoOrderDto>();
        for (var i = 0; i < 3; i++)
        {
            orders.Add(await SettlePayoOrderAsync(setup));
        }

        var first = await Client.GetFromJsonAsync<RestoKeysetPageDto<PendingPayoReleaseRowDto>>(
            "/api/resto/orders/pending-releases?limit=2", TestJson.Options);
        first!.Items.Should().HaveCount(2);
        first.NextAfterId.Should().NotBeNull();

        var second = await Client.GetFromJsonAsync<RestoKeysetPageDto<PendingPayoReleaseRowDto>>(
            $"/api/resto/orders/pending-releases?limit=2&afterOrderId={first.NextAfterId}", TestJson.Options);
        second!.Items.Should().HaveCount(1);
        second.NextAfterId.Should().BeNull();

        var seen = first.Items.Concat(second.Items).Select(r => r.OrderId).ToList();
        seen.Should().BeEquivalentTo(orders.Select(o => o.Id));
    }

    [Fact]
    public async Task A_cashier_in_another_branch_sees_no_pending_releases_and_cannot_scope_to_this_branch()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        await SettlePayoOrderAsync(setup);

        var otherBranch = await CreateBranchAsync(name: "Branch B", code: "BRB");
        var cashierToken = await AddTenantUserTokenAsync("cashier.other@testco.com", UserRole.Cashier, otherBranch.Id);
        Authorize(cashierToken);

        var page = await Client.GetFromJsonAsync<RestoKeysetPageDto<PendingPayoReleaseRowDto>>(
            "/api/resto/orders/pending-releases", TestJson.Options);
        page!.Items.Should().BeEmpty();

        var scoped = await Client.GetAsync($"/api/resto/orders/pending-releases?branchId={setup.BranchId}");
        scoped.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unacknowledged_tickets_report_only_released_items_that_are_old_pending_and_not_voided()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var order = await SettlePayoOrderAsync(setup);
        var roundId = order.Rounds.Single().Id;

        // Released now: under the five-minute threshold, so not reported yet.
        var release = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/rounds/{roundId}/release", new RestoStructuralRequest(order.RowVersion));
        release.EnsureSuccessStatusCode();
        var released = (await release.Content.ReadFromJsonAsync<RestoOrderDto>(TestJson.Options))!;
        var itemId = released.Rounds.Single().Items.Single().Id;

        (await UnacknowledgedAsync(setup)).Items.Should().BeEmpty("released less than the threshold ago");

        await BackdateReleaseAsync(setup.TenantId, order.Id, minutes: 10);

        var old = await UnacknowledgedAsync(setup);
        old.Items.Should().ContainSingle();
        old.Items[0].ItemId.Should().Be(itemId);
        old.Items[0].AgeSeconds.Should().BeGreaterThanOrEqualTo(600);

        // A kitchen acknowledgement removes it from the alert.
        await AcknowledgeItemAsync(itemId, await GetUserIdFromTokenAsync(setup.OwnerToken));
        (await UnacknowledgedAsync(setup)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_voided_released_item_is_not_reported_as_unacknowledged()
    {
        // Only an open Bill-Out order can have a released item voided; a settled Pay-as-you-order order cannot.
        var setup = await SetupBillOutAsync(openingStock: 10m);
        var order = await OpenOrderAsync(setup);
        order = await AddRoundAsync(order);
        var roundId = order.Rounds.Single().Id;
        order = await AddItemAsync(order, roundId, setup, quantity: 1m);
        order = await ReleaseRoundAsync(order, roundId);
        var itemId = order.Rounds.Single().Items.Single().Id;

        await BackdateReleaseAsync(setup.TenantId, order.Id, minutes: 10);
        (await UnacknowledgedAsync(setup)).Items.Should().ContainSingle("the released item is old and still pending");

        var voidItem = await Client.PostAsJsonAsync(
            $"/api/resto/orders/{order.Id}/items/{itemId}/void",
            new VoidRestoItemRequest(order.RowVersion, "Entered by mistake", Approval: null));
        voidItem.EnsureSuccessStatusCode();

        (await UnacknowledgedAsync(setup)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_settled_order_still_waiting_for_release_is_not_an_unacknowledged_ticket()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        await SettlePayoOrderAsync(setup);

        (await UnacknowledgedAsync(setup)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task One_failing_order_does_not_stop_a_later_eligible_order_in_the_same_tenant()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var failing = await SettlePayoOrderAsync(setup);   // settled first: it is at the head of the queue
        var eligible = await SettlePayoOrderAsync(setup);
        var failingRound = failing.Rounds.Single().Id;
        var eligibleRound = eligible.Rounds.Single().Id;

        await InjectOrderUpdateFailureAsync(setup.TenantId, failing.Id);
        try
        {
            var cycle = await RunWorkerAsync(setup.TenantId);

            cycle.Failed.Should().Be(1);
            cycle.Failures.Should().ContainSingle().Which.OrderId.Should().Be(failing.Id);
            cycle.Released.Should().Be(1, "the later order must still be released");
            (await GetRoundAsync(setup.TenantId, eligibleRound)).Status.Should().Be(RestoOrderRoundStatus.Released);
            (await GetRoundAsync(setup.TenantId, failingRound)).Status.Should().Be(RestoOrderRoundStatus.Draft);

            // The failing order is now in backoff: the next pass skips it rather than retrying it at once.
            var next = await RunWorkerAsync(setup.TenantId);
            next.Failed.Should().Be(0);
            next.BackedOff.Should().Be(1);
        }
        finally
        {
            await DropOrderUpdateFailureAsync(setup.TenantId);
        }
    }

    [Fact]
    public async Task Cancelling_mid_batch_leaves_no_partial_release_and_a_later_pass_recovers_it()
    {
        var setup = await SetupPayoAsync(openingStock: 10m);
        var first = await SettlePayoOrderAsync(setup);   // processed first; slowed down so cancellation lands inside it
        var second = await SettlePayoOrderAsync(setup);
        var firstRound = first.Rounds.Single().Id;
        var secondRound = second.Rounds.Single().Id;

        // Another connection holds the first round's row lock, so the worker blocks inside that order until it is cancelled.
        await using var blocker = await Factory.OpenTenantConnectionAsync(setup.TenantId);
        await using var blockerTx = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var hold = blocker.CreateCommand())
        {
            hold.Transaction = blockerTx;
            hold.CommandText = $"UPDATE RestoOrderRounds SET RoundNumber = RoundNumber WHERE Id = '{firstRound}'";
            await hold.ExecuteNonQueryAsync();
        }

        var cancelled = false;
        try
        {
            using var scope = Factory.Services.CreateScope();
            var worker = scope.ServiceProvider.GetRequiredService<IPayoReleaseWorkerService>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await worker.ReleaseDueRoundsAsync(setup.TenantId, cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                cancelled = true;
            }
        }
        finally
        {
            await blockerTx.RollbackAsync();
        }

        cancelled.Should().BeTrue("the pass must stop because it was cancelled, not finish");
        (await GetRoundAsync(setup.TenantId, firstRound)).Status.Should().Be(RestoOrderRoundStatus.Draft, "the interrupted round is rolled back");
        (await GetRoundAsync(setup.TenantId, secondRound)).Status.Should().Be(RestoOrderRoundStatus.Draft);
        (await ItemKitchenStatusesAsync(setup.TenantId, firstRound)).Should().OnlyContain(status => status == null, "no item was sent to the kitchen");

        // A cancellation is not a failure: nothing is backed off, so an ordinary pass releases both orders.
        var recovery = await RunWorkerAsync(setup.TenantId);
        recovery.Released.Should().Be(2);
        recovery.Failed.Should().Be(0);
        recovery.BackedOff.Should().Be(0);
    }

    [Fact]
    public async Task Refused_orders_at_the_head_of_the_queue_cannot_starve_a_later_order_across_polling_cycles()
    {
        var setup = await SetupPayoAsync(openingStock: 30m);
        for (var i = 0; i < 3; i++)
        {
            var refused = await SettlePayoOrderAsync(setup);
            var voided = await Client.PostAsJsonAsync($"/api/sales/{refused.SaleId}/void", new VoidSaleRequest("Cancelled"));
            voided.EnsureSuccessStatusCode();
        }

        var eligible = await SettlePayoOrderAsync(setup);
        var eligibleRound = eligible.Rounds.Single().Id;

        // Two pages of one row per cycle, so the three refused orders fill the first cycle's whole budget.
        using var scope = Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
        var worker = new PayoReleaseWorkerService(
            factory,
            new RestoReconciliationOptions { BatchSize = 1, ReleaseGraceSeconds = 0 },
            scope.ServiceProvider.GetRequiredService<FailureBackoff<(Guid TenantId, Guid OrderId)>>(),
            scope.ServiceProvider.GetRequiredService<PayoReleaseCursorStore>(),
            TimeProvider.System,
            maxPagesPerCycle: 2);

        var firstCycle = await worker.ReleaseDueRoundsAsync(setup.TenantId);
        firstCycle.Released.Should().Be(0, "the budget ran out on refused orders");
        (await GetRoundAsync(setup.TenantId, eligibleRound)).Status.Should().Be(RestoOrderRoundStatus.Draft);

        var secondCycle = await worker.ReleaseDueRoundsAsync(setup.TenantId);
        secondCycle.Released.Should().Be(1, "the next cycle resumes past the refused orders instead of restarting at the head");
        (await GetRoundAsync(setup.TenantId, eligibleRound)).Status.Should().Be(RestoOrderRoundStatus.Released);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>Runs one worker pass for a tenant in its own DI scope, the way a separate instance would.</summary>
    private async Task<PayoReleaseCycleResult> RunWorkerAsync(Guid tenantId)
    {
        using var scope = Factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<IPayoReleaseWorkerService>();
        return await worker.ReleaseDueRoundsAsync(tenantId);
    }

    private async Task<RestoKeysetPageDto<UnacknowledgedTicketRowDto>> UnacknowledgedAsync(Setup setup)
    {
        Authorize(setup.OwnerToken);
        var page = await Client.GetFromJsonAsync<RestoKeysetPageDto<UnacknowledgedTicketRowDto>>(
            "/api/resto/orders/unacknowledged-tickets", TestJson.Options);
        return page!;
    }

    private Task<RoundState> GetRoundAsync(Guid tenantId, Guid roundId) =>
        InTenantScopeAsync(tenantId, db => db.RestoOrderRounds.AsNoTracking()
            .Where(r => r.Id == roundId)
            .Select(r => new RoundState(r.Status, r.ReleasedAtUtc, r.ReleasedByUserId))
            .SingleAsync());

    private Task BackdateReleaseAsync(Guid tenantId, Guid orderId, int minutes) =>
        InTenantScopeAsync(tenantId, async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE RestoOrderRounds SET ReleasedAtUtc = DATEADD(MINUTE, -{minutes}, ReleasedAtUtc) WHERE RestoOrderId = {orderId}");
            return true;
        });

    private async Task<string> SetDatabaseNameAsync(Guid tenantId, string databaseName)
    {
        var original = await InPlatformScopeAsync(platform => platform.TenantDatabases.AsNoTracking()
            .Where(t => t.TenantId == tenantId).Select(t => t.DatabaseName).SingleAsync());
        await InPlatformScopeAsync(async platform =>
        {
            await platform.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE TenantDatabases SET DatabaseName = {databaseName} WHERE TenantId = {tenantId}");
            return true;
        });
        return original;
    }

    /// <summary>
    /// Makes releasing this order's round fail with a real constraint violation. The rule only rejects the Released status
    /// (2) on this order's round, and existing Draft rows satisfy it, so the constraint validates cleanly and only the
    /// targeted order's release fails.
    /// </summary>
    private Task InjectOrderUpdateFailureAsync(Guid tenantId, Guid orderId) => InTenantScopeAsync(tenantId, async db =>
    {
        // Guid literal from the test itself, not user input.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE RestoOrderRounds WITH CHECK ADD CONSTRAINT ck_test_fail_order CHECK (Status <> 2 OR RestoOrderId <> '" + orderId + "');");
        return true;
    });

    private Task DropOrderUpdateFailureAsync(Guid tenantId) => InTenantScopeAsync(tenantId, async db =>
    {
        await db.Database.ExecuteSqlRawAsync(
            "IF OBJECT_ID('ck_test_fail_order', 'C') IS NOT NULL ALTER TABLE RestoOrderRounds DROP CONSTRAINT ck_test_fail_order;");
        return true;
    });

    private Task<List<RestoKitchenStatus?>> ItemKitchenStatusesAsync(Guid tenantId, Guid roundId) =>
        InTenantScopeAsync(tenantId, db => db.RestoOrderItems.AsNoTracking()
            .Where(i => i.RestoOrderRoundId == roundId)
            .Select(i => i.KitchenStatus)
            .ToListAsync());

    private sealed record RoundState(RestoOrderRoundStatus Status, DateTime? ReleasedAtUtc, Guid? ReleasedByUserId);
}
