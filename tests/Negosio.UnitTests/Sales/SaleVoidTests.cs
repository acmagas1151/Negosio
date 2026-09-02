using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Sales;

public class SaleVoidTests
{
    private static Sale CompletedSale()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());
        sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 2m, DiscountType.None, 0m, 150m, 0m, 0m, 150m, 40m);
        sale.Complete(150m, 0m, 0m, 150m, 150m, 0m);
        return sale;
    }

    [Fact]
    public void Void_transitions_completed_to_voided_and_stamps_audit_fields()
    {
        var sale = CompletedSale();
        var voidedBy = Guid.NewGuid();
        var approvedBy = Guid.NewGuid();
        var nowUtc = new DateTime(2026, 9, 2, 10, 30, 0, DateTimeKind.Utc);

        sale.Void(voidedBy, "Wrong payment method", approvedBy, nowUtc);

        sale.Status.Should().Be(SaleStatus.Voided);
        sale.VoidedByUserId.Should().Be(voidedBy);
        sale.ApprovedByUserId.Should().Be(approvedBy);
        sale.VoidReason.Should().Be("Wrong payment method");
        sale.VoidedAtUtc.Should().Be(nowUtc);
    }

    [Fact]
    public void Void_uses_the_passed_timestamp_not_wall_clock()
    {
        var sale = CompletedSale();
        var farFuture = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        sale.Void(Guid.NewGuid(), "Timestamp check", null, farFuture);

        sale.VoidedAtUtc.Should().Be(farFuture); // proves Void() never calls DateTime.UtcNow itself
    }

    [Fact]
    public void Void_direct_leaves_approver_null()
    {
        var sale = CompletedSale();

        sale.Void(Guid.NewGuid(), "Duplicate transaction", approvedByUserId: null, DateTime.UtcNow);

        sale.ApprovedByUserId.Should().BeNull();
    }

    [Fact]
    public void Void_twice_throws()
    {
        var sale = CompletedSale();
        sale.Void(Guid.NewGuid(), "Wrong payment method", null, DateTime.UtcNow);

        var act = () => sale.Void(Guid.NewGuid(), "Second attempt", null, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Void_requires_a_non_blank_reason()
    {
        var sale = CompletedSale();

        var act = () => sale.Void(Guid.NewGuid(), "   ", null, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Void_before_completion_throws()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());

        var act = () => sale.Void(Guid.NewGuid(), "Never completed", null, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
}
