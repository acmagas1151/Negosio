using FluentAssertions;
using Negosio.Domain.Entities;

namespace Negosio.UnitTests.Domain;

public class InventoryEntityTests
{
    [Fact]
    public void ApplyAdjustment_returns_before_and_after()
    {
        var inventory = BranchInventory.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reorderLevel: 5m);

        var (before, after) = inventory.ApplyAdjustment(12.5m);

        before.Should().Be(0m);
        after.Should().Be(12.5m);
        inventory.QuantityOnHand.Should().Be(12.5m);
    }

    [Fact]
    public void ApplyAdjustment_cannot_drive_quantity_negative()
    {
        var inventory = BranchInventory.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reorderLevel: 0m);
        inventory.ApplyAdjustment(3m);

        var act = () => inventory.ApplyAdjustment(-5m);

        act.Should().Throw<InvalidOperationException>();
        inventory.QuantityOnHand.Should().Be(3m);
    }

    [Fact]
    public void IsLow_is_true_at_or_below_reorder_level()
    {
        var inventory = BranchInventory.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reorderLevel: 10m);
        inventory.ApplyAdjustment(10m);

        inventory.IsLow.Should().BeTrue();

        inventory.ApplyAdjustment(1m);
        inventory.IsLow.Should().BeFalse();
    }
}
