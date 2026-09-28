using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderItemModifierTests
{
    [Fact]
    public void AddModifier_on_an_item_freezes_the_given_snapshot_values()
    {
        var tenantId = Guid.NewGuid();
        var order = RestoOrder.OpenBillOut(tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        var round = order.OpenNextRound();
        var item = round.AddItem(
            Guid.NewGuid(), "Burger", null, Guid.NewGuid(), "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: null);

        var modifier = item.AddModifier("Add-ons", "Extra cheese", 20m);

        modifier.TenantId.Should().Be(tenantId);
        modifier.RestoOrderItemId.Should().Be(item.Id);
        modifier.ModifierGroupNameSnapshot.Should().Be("Add-ons");
        modifier.ModifierOptionNameSnapshot.Should().Be("Extra cheese");
        modifier.PriceDeltaSnapshot.Should().Be(20m);
    }
}
