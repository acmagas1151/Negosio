using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Domain.Entities;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Resto;

public class RestoOrderTableOccupancyTests : IntegrationTest
{
    public RestoOrderTableOccupancyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task A_second_open_order_against_the_same_table_is_rejected_at_the_database_level()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);

        var act = () => InScopeAsync(async db =>
        {
            var table = RestoTable.Create(login.User.TenantId, branchId, "Table 1");
            db.RestoTables.Add(table);
            await db.SaveChangesAsync();

            var firstOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(firstOrder);
            await db.SaveChangesAsync();

            var secondOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(secondOrder);
            await db.SaveChangesAsync();
            return true;
        });

        await act.Should().ThrowAsync<DbUpdateException>(
            "the filtered unique index must reject a second Open order on the same table");
    }

    [Fact]
    public async Task A_new_open_order_is_allowed_once_the_first_is_settled()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);

        var secondOrderId = await InScopeAsync(async db =>
        {
            var table = RestoTable.Create(login.User.TenantId, branchId, "Table 2");
            db.RestoTables.Add(table);
            await db.SaveChangesAsync();

            var firstOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(firstOrder);
            await db.SaveChangesAsync();

            // Freed via Cancel rather than Settle: Settle now requires a real Sale row to satisfy the
            // RestoOrder.SaleId -> Sale FK, and Cancel equally flips Status away from Open (both free
            // the table's occupancy) without needing a Sale to exist.
            firstOrder.Cancel(login.User.Id, "test cleanup", DateTime.UtcNow);
            await db.SaveChangesAsync();

            var secondOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(secondOrder);
            await db.SaveChangesAsync();
            return secondOrder.Id;
        });

        secondOrderId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Two_open_pay_as_you_order_orders_with_no_table_are_both_allowed()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);

        var act = () => InScopeAsync(async db =>
        {
            var firstOrder = RestoOrder.OpenPayAsYouOrder(login.User.TenantId, branchId, session.Id, login.User.Id, "Counter 1");
            db.RestoOrders.Add(firstOrder);
            await db.SaveChangesAsync();

            var secondOrder = RestoOrder.OpenPayAsYouOrder(login.User.TenantId, branchId, session.Id, login.User.Id, "Counter 2");
            db.RestoOrders.Add(secondOrder);
            await db.SaveChangesAsync();
            return true;
        });

        // SQL Server treats multiple NULLs as non-conflicting for a unique index, and the filtered
        // index explicitly adds "AND [TableId] IS NOT NULL" so a null-table (Pay-as-you-order) order
        // is never blocked by it. Proves that clause actually works.
        await act.Should().NotThrowAsync();
    }
}
