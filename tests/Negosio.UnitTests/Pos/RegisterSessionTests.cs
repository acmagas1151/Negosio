using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Pos;

public class RegisterSessionTests
{
    [Fact]
    public void Open_starts_in_the_open_state()
    {
        var session = RegisterSession.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 500m);

        session.Status.Should().Be(RegisterSessionStatus.Open);
        session.OpeningCash.Should().Be(500m);
        session.ClosedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Open_rejects_negative_opening_cash()
    {
        var act = () => RegisterSession.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Close_records_the_expected_and_difference()
    {
        var session = RegisterSession.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 500m);

        session.Close(Guid.NewGuid(), closingCash: 1180m, expectedCash: 1200m);

        session.Status.Should().Be(RegisterSessionStatus.Closed);
        session.ExpectedCash.Should().Be(1200m);
        session.CashDifference.Should().Be(-20m);
        session.ClosedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Close_twice_throws()
    {
        var session = RegisterSession.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0m);
        session.Close(Guid.NewGuid(), 0m, 0m);

        var act = () => session.Close(Guid.NewGuid(), 0m, 0m);

        act.Should().Throw<InvalidOperationException>();
    }
}

public class SaleAggregateTests
{
    [Fact]
    public void MarkReturned_flips_to_partial_then_full()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "00000001", Guid.NewGuid(), Guid.NewGuid());
        var item = sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 2m, DiscountType.None, 0m, 150m, 0m, 0m, 150m, 40m);
        sale.Complete(150m, 0m, 0m, 150m, 150m, 0m);

        item.RecordReturn(1m);
        sale.MarkReturned();
        sale.Status.Should().Be(SaleStatus.PartiallyRefunded);

        item.RecordReturn(1m);
        sale.MarkReturned();
        sale.Status.Should().Be(SaleStatus.Refunded);
    }

    [Fact]
    public void RecordReturn_cannot_exceed_the_line_quantity()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "00000001", Guid.NewGuid(), Guid.NewGuid());
        var item = sale.AddItem(Guid.NewGuid(), "Coke", null, null, null, 75m, 2m, DiscountType.None, 0m, 150m, 0m, 0m, 150m, null);

        var act = () => item.RecordReturn(3m);

        act.Should().Throw<InvalidOperationException>();
    }
}
