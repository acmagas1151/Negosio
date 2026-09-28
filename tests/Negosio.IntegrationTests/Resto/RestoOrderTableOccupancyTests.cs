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

            firstOrder.Settle(Guid.NewGuid(), DateTime.UtcNow);
            await db.SaveChangesAsync();

            var secondOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(secondOrder);
            await db.SaveChangesAsync();
            return secondOrder.Id;
        });

        secondOrderId.Should().NotBeEmpty();
    }
}
