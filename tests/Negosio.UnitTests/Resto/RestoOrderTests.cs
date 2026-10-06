using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();
    private static readonly Guid RegisterSessionId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TableId = Guid.NewGuid();

    [Fact]
    public void OpenPayAsYouOrder_has_no_table_and_starts_open()
    {
        var order = RestoOrder.OpenPayAsYouOrder(TenantId, BranchId, RegisterSessionId, UserId, "Counter 3", true);

        order.ServiceType.Should().Be(RestoServiceType.PayAsYouOrder);
        order.TableId.Should().BeNull();
        order.DisplayLabel.Should().Be("Counter 3");
        order.Status.Should().Be(RestoOrderStatus.Open);
        order.Rounds.Should().BeEmpty();
    }

    [Fact]
    public void OpenBillOut_requires_a_table()
    {
        var act = () => RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, Guid.Empty, null, true);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OpenBillOut_with_a_table_starts_open()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null, true);

        order.ServiceType.Should().Be(RestoServiceType.BillOut);
        order.TableId.Should().Be(TableId);
        order.Status.Should().Be(RestoOrderStatus.Open);
    }

    [Fact]
    public void OpenNextRound_numbers_rounds_sequentially_starting_at_one()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null, true);

        var round1 = order.OpenNextRound();
        var round2 = order.OpenNextRound();

        round1.RoundNumber.Should().Be(1);
        round2.RoundNumber.Should().Be(2);
        order.Rounds.Should().HaveCount(2);
    }

    [Fact]
    public void Settle_requires_the_order_to_be_open()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null, true);
        order.Settle(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        var act = () => order.Settle(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Settle_sets_SaleId_and_flips_status()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null, true);
        var saleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        order.Settle(saleId, Guid.NewGuid(), now);

        order.Status.Should().Be(RestoOrderStatus.Settled);
        order.SaleId.Should().Be(saleId);
        order.SettledAtUtc.Should().Be(now);
    }

    [Fact]
    public void Cancel_succeeds_when_no_round_has_been_released()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null, true);
        order.OpenNextRound();
        var now = DateTime.UtcNow;

        order.Cancel(UserId, "Customer left", now);

        order.Status.Should().Be(RestoOrderStatus.Cancelled);
        order.CancelledByUserId.Should().Be(UserId);
        order.CancelReason.Should().Be("Customer left");
        order.CancelledAtUtc.Should().Be(now);
    }

    [Fact]
    public void OpenNextRound_is_rejected_once_the_order_has_been_settled()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null, true);
        order.Settle(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        var act = () => order.OpenNextRound();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_is_rejected_once_any_round_has_been_released()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null, true);
        var round = order.OpenNextRound();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => order.Cancel(UserId, "Too late", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
}
