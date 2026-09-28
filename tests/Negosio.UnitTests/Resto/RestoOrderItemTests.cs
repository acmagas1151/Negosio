using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderItemTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ProductVariantId = Guid.NewGuid();
    private static readonly Guid StationId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static (RestoOrderRound Round, RestoOrderItem Item) NewItem()
    {
        var order = RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null);
        var round = order.OpenNextRound();
        var item = round.AddItem(
            ProductVariantId, "Burger", "Regular", StationId, "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: "No onions");
        return (round, item);
    }

    [Fact]
    public void AddItem_freezes_every_snapshot_field_and_has_no_KitchenStatus_yet()
    {
        var (_, item) = NewItem();

        item.ProductNameSnapshot.Should().Be("Burger");
        item.VariantNameSnapshot.Should().Be("Regular");
        item.StationId.Should().Be(StationId);
        item.StationNameSnapshot.Should().Be("Kitchen");
        item.UnitPriceSnapshot.Should().Be(150m);
        item.GrossAmount.Should().Be(150m);
        item.NetAmount.Should().Be(150m);
        item.KitchenNote.Should().Be("No onions");
        item.KitchenStatus.Should().BeNull();
        item.VoidedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Releasing_the_round_sets_KitchenStatus_to_Pending()
    {
        var (round, item) = NewItem();

        round.Release(UserId, DateTime.UtcNow);

        item.KitchenStatus.Should().Be(RestoKitchenStatus.Pending);
    }

    [Fact]
    public void Status_transitions_must_happen_in_order()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => item.MarkReady(UserId, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>("an item cannot skip Acknowledged");
    }

    [Fact]
    public void Acknowledge_then_Ready_then_Served_records_each_actor_and_timestamp()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);
        var t1 = DateTime.UtcNow;
        var t2 = t1.AddMinutes(2);
        var t3 = t1.AddMinutes(5);

        item.Acknowledge(UserId, t1);
        item.MarkReady(UserId, t2);
        item.MarkServed(UserId, t3);

        item.KitchenStatus.Should().Be(RestoKitchenStatus.Served);
        item.AcknowledgedAtUtc.Should().Be(t1);
        item.ReadyAtUtc.Should().Be(t2);
        item.ServedAtUtc.Should().Be(t3);
        item.ServedByUserId.Should().Be(UserId);
    }

    [Fact]
    public void Void_is_allowed_at_any_KitchenStatus_and_never_deletes_the_item()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);
        item.Acknowledge(UserId, DateTime.UtcNow);
        var now = DateTime.UtcNow;

        item.Void(UserId, "Kitchen made a mistake", approvedByUserId: null, now);

        item.VoidedAtUtc.Should().Be(now);
        item.VoidedByUserId.Should().Be(UserId);
        item.VoidReason.Should().Be("Kitchen made a mistake");
        item.KitchenStatus.Should().Be(RestoKitchenStatus.Acknowledged, "void never rewrites the kitchen-status history");
    }

    [Fact]
    public void Void_cannot_be_called_twice()
    {
        var (_, item) = NewItem();
        item.Void(UserId, "Mistake", null, DateTime.UtcNow);

        var act = () => item.Void(UserId, "Again", null, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddModifier_appends_a_frozen_modifier_line()
    {
        var (_, item) = NewItem();

        var modifier = item.AddModifier("Add-ons", "Extra cheese", 20m);

        item.Modifiers.Should().ContainSingle().Which.Should().BeSameAs(modifier);
        modifier.ModifierGroupNameSnapshot.Should().Be("Add-ons");
        modifier.ModifierOptionNameSnapshot.Should().Be("Extra cheese");
        modifier.PriceDeltaSnapshot.Should().Be(20m);
    }
}
