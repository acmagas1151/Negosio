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
        var order = RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null, true);
        var round = order.OpenNextRound();
        var item = round.AddItem(
            ProductVariantId, "Burger", "Regular", StationId, "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: "No onions", discountKind: Negosio.Domain.Enums.DiscountType.None, discountValue: 0m, discountApprovedByUserId: null, costPriceSnapshot: null);
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

    [Fact]
    public void AddModifier_is_rejected_once_the_item_has_been_released_to_the_kitchen()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => item.AddModifier("Add-ons", "Extra cheese", 20m);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddModifier_rejects_a_blank_group_name()
    {
        var (_, item) = NewItem();

        var act = () => item.AddModifier("   ", "Extra cheese", 20m);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddModifier_rejects_a_blank_option_name()
    {
        var (_, item) = NewItem();

        var act = () => item.AddModifier("Add-ons", "   ", 20m);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddModifier_rejects_a_negative_price_delta()
    {
        var (_, item) = NewItem();

        var act = () => item.AddModifier("Add-ons", "Extra cheese", -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Acknowledge_is_rejected_once_the_item_has_been_voided_while_pending()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);
        item.Void(UserId, "Kitchen made a mistake", approvedByUserId: null, DateTime.UtcNow);

        var act = () => item.Acknowledge(UserId, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>("a voided item must not be progressable");
    }

    [Fact]
    public void Create_rejects_a_blank_product_name()
    {
        var order = RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null, true);
        var round = order.OpenNextRound();

        var act = () => round.AddItem(
            ProductVariantId, "   ", "Regular", StationId, "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: null, discountKind: Negosio.Domain.Enums.DiscountType.None, discountValue: 0m, discountApprovedByUserId: null, costPriceSnapshot: null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_a_negative_gross_amount()
    {
        var order = RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null, true);
        var round = order.OpenNextRound();

        var act = () => round.AddItem(
            ProductVariantId, "Burger", "Regular", StationId, "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: -1m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: null, discountKind: Negosio.Domain.Enums.DiscountType.None, discountValue: 0m, discountApprovedByUserId: null, costPriceSnapshot: null);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
