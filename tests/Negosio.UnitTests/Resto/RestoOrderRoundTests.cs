using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderRoundTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid StationId = Guid.NewGuid();
    private static readonly Guid ProductVariantId = Guid.NewGuid();

    private static RestoOrder NewOrder() =>
        RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null);

    [Fact]
    public void OpenNextRound_starts_in_Draft_with_no_items()
    {
        var order = NewOrder();

        var round = order.OpenNextRound();

        round.RestoOrderId.Should().Be(order.Id);
        round.RoundNumber.Should().Be(1);
        round.Status.Should().Be(RestoOrderRoundStatus.Draft);
        round.Items.Should().BeEmpty();
    }

    [Fact]
    public void Release_flips_status_and_puts_every_item_into_Pending()
    {
        var round = NewOrder().OpenNextRound();
        var item = round.AddItem(
            ProductVariantId, "Burger", null, StationId, "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: null);
        var now = DateTime.UtcNow;

        round.Release(UserId, now);

        round.Status.Should().Be(RestoOrderRoundStatus.Released);
        round.ReleasedByUserId.Should().Be(UserId);
        round.ReleasedAtUtc.Should().Be(now);
        item.KitchenStatus.Should().Be(RestoKitchenStatus.Pending);
    }

    [Fact]
    public void Release_is_idempotent()
    {
        var round = NewOrder().OpenNextRound();
        var firstUser = UserId;
        var firstTime = DateTime.UtcNow;
        round.Release(firstUser, firstTime);

        round.Release(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));

        round.ReleasedByUserId.Should().Be(firstUser, "a retried release must not overwrite who actually released it");
        round.ReleasedAtUtc.Should().Be(firstTime);
    }

    [Fact]
    public void AddItem_is_rejected_once_the_round_has_been_released()
    {
        var round = NewOrder().OpenNextRound();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => round.AddItem(
            ProductVariantId, "Burger", null, StationId, "Kitchen",
            150m, 12m, 150m, 0m, 18m, 150m, 1m, null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void VoidWhole_flips_status_from_Draft()
    {
        var round = NewOrder().OpenNextRound();

        round.VoidWhole();

        round.Status.Should().Be(RestoOrderRoundStatus.Voided);
    }

    [Fact]
    public void VoidWhole_is_rejected_once_the_round_has_been_released()
    {
        var round = NewOrder().OpenNextRound();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => round.VoidWhole();

        act.Should().Throw<InvalidOperationException>();
    }
}
