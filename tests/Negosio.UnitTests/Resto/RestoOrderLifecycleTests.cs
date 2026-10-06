using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderLifecycleTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static RestoOrder BillOut(bool pricesIncludeTax = false) =>
        RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null, pricesIncludeTax);

    private static RestoOrder Payo(bool pricesIncludeTax = false) =>
        RestoOrder.OpenPayAsYouOrder(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, null, pricesIncludeTax);

    [Fact]
    public void Open_freezes_the_tenant_tax_mode_on_the_order()
    {
        BillOut(pricesIncludeTax: true).PricesIncludeTaxSnapshot.Should().BeTrue();
        Payo(pricesIncludeTax: false).PricesIncludeTaxSnapshot.Should().BeFalse();
    }

    [Fact]
    public void Unpaid_close_requires_a_bill_out_order()
    {
        var order = Payo();
        var round = order.OpenNextRound();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => order.UnpaidClose(UserId, "Walked out", null, Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Unpaid_close_requires_at_least_one_released_round()
    {
        var order = BillOut();
        order.OpenNextRound();

        var act = () => order.UnpaidClose(UserId, "Walked out", null, Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Unpaid_close_moves_an_open_order_to_unpaid_closed_with_audit_fields()
    {
        var order = BillOut();
        order.OpenNextRound().Release(UserId, DateTime.UtcNow);
        var approver = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        order.UnpaidClose(UserId, "  Customer left  ", approver, requestId, now);

        order.Status.Should().Be(RestoOrderStatus.UnpaidClosed);
        order.UnpaidClosureReason.Should().Be("Customer left");
        order.UnpaidClosureApprovedByUserId.Should().Be(approver);
        order.UnpaidClosureRequestId.Should().Be(requestId);
        order.UnpaidClosedAtUtc.Should().Be(now);
        order.SaleId.Should().BeNull("unpaid closure never creates a sale");
    }

    [Fact]
    public void Settle_records_the_settlement_request_key()
    {
        var order = BillOut();
        var key = Guid.NewGuid();

        order.Settle(Guid.NewGuid(), key, DateTime.UtcNow);

        order.SettlementRequestId.Should().Be(key);
    }

    [Fact]
    public void A_closed_unpaid_order_cannot_then_be_settled()
    {
        var order = BillOut();
        order.OpenNextRound().Release(UserId, DateTime.UtcNow);
        order.UnpaidClose(UserId, "Walked out", null, Guid.NewGuid(), DateTime.UtcNow);

        var act = () => order.Settle(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_order_with_a_released_round_cannot_be_cancelled()
    {
        var order = BillOut();
        order.OpenNextRound().Release(UserId, DateTime.UtcNow);

        var act = () => order.Cancel(UserId, "Too late", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
}
