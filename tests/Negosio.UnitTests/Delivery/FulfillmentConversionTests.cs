using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class FulfillmentConversionTests
{
    private static FulfillmentConversion Make(
        FulfillmentMethod from = FulfillmentMethod.Delivery,
        FulfillmentMethod to = FulfillmentMethod.Pickup,
        decimal quantity = 2m) =>
        FulfillmentConversion.Record(
            tenantId: Guid.NewGuid(), saleId: Guid.NewGuid(), saleItemId: Guid.NewGuid(),
            quantity: quantity, fromMethod: from, toMethod: to,
            sourceRecordId: Guid.NewGuid(), replacementRecordId: Guid.NewGuid(),
            reason: "  Customer picked up instead  ", createdByUserId: Guid.NewGuid(),
            createdAtUtc: new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Record_captures_the_conversion()
    {
        var e = Make();

        e.FromMethod.Should().Be(FulfillmentMethod.Delivery);
        e.ToMethod.Should().Be(FulfillmentMethod.Pickup);
        e.Quantity.Should().Be(2m);
        e.Reason.Should().Be("Customer picked up instead");
        e.SourceRecordId.Should().NotBeNull();
        e.ReplacementRecordId.Should().NotBeNull();
    }

    [Fact]
    public void Record_allows_a_null_replacement_for_a_release_back_to_unscheduled()
    {
        var e = FulfillmentConversion.Record(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3m,
            FulfillmentMethod.Delivery, FulfillmentMethod.Delivery,
            sourceRecordId: Guid.NewGuid(), replacementRecordId: null,
            reason: "Deliver later", createdByUserId: Guid.NewGuid(), createdAtUtc: DateTime.UtcNow);

        e.ReplacementRecordId.Should().BeNull();
        e.FromMethod.Should().Be(e.ToMethod); // a release is a same-method event, recorded for the audit trail
    }

    [Fact]
    public void Record_rejects_a_non_positive_quantity()
    {
        var act = () => Make(quantity: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Record_rejects_TakeNow_on_either_side()
    {
        var fromTakeNow = () => Make(from: FulfillmentMethod.TakeNow);
        var toTakeNow = () => Make(to: FulfillmentMethod.TakeNow);

        fromTakeNow.Should().Throw<InvalidOperationException>();
        toTakeNow.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Record_requires_a_reason()
    {
        var act = () => FulfillmentConversion.Record(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1m,
            FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, Guid.NewGuid(), Guid.NewGuid(),
            reason: "   ", createdByUserId: Guid.NewGuid(), createdAtUtc: DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
