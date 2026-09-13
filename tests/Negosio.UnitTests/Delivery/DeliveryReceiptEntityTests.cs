using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class DeliveryReceiptEntityTests
{
    private static DeliveryReceipt MakeDelivery() => DeliveryReceipt.CreateDelivery(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
        sequenceNumber: 1, scheduledDate: new DateOnly(2026, 9, 15),
        "  Juan Dela Cruz ", " 123 Ayala Ave, Makati ", " 0917 111 2222 ", "  Leave at guardhouse ",
        Guid.NewGuid(), " Cashier One ");

    private static DeliveryReceipt MakePickup() => DeliveryReceipt.CreatePickup(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
        sequenceNumber: 1, scheduledDate: new DateOnly(2026, 9, 15),
        recipientName: "  Juan Dela Cruz ", contactNumber: " 0917 111 2222 ", notes: "  Ring the bell ",
        preparedByUserId: Guid.NewGuid(), preparedByNameSnapshot: " Cashier One ");

    [Fact]
    public void Create_trims_and_keeps_snapshot_fields()
    {
        var dr = MakeDelivery();
        dr.RecipientName.Should().Be("Juan Dela Cruz");
        dr.DeliveryAddress.Should().Be("123 Ayala Ave, Makati");
        dr.ContactNumber.Should().Be("0917 111 2222");
        dr.RelatedSaleNumber.Should().Be("0000042");
        dr.PreparedByNameSnapshot.Should().Be("Cashier One");
    }

    [Fact]
    public void Create_requires_recipient_and_address()
    {
        var blankName = () => DeliveryReceipt.CreateDelivery(Guid.NewGuid(), Guid.NewGuid(), null, null,
            sequenceNumber: 1, scheduledDate: new DateOnly(2026, 9, 15),
            "   ", "addr", null, null, Guid.NewGuid(), "x");
        blankName.Should().Throw<ArgumentException>();
        var blankAddr = () => DeliveryReceipt.CreateDelivery(Guid.NewGuid(), Guid.NewGuid(), null, null,
            sequenceNumber: 1, scheduledDate: new DateOnly(2026, 9, 15),
            "Juan", "   ", null, null, Guid.NewGuid(), "x");
        blankAddr.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddItem_snapshots_line_and_rejects_zero_qty()
    {
        var dr = MakeDelivery();
        var saleItemId = Guid.NewGuid();

        dr.AddItem(saleItemId, "Coke 1.5L", null, 3m, 85m);
        dr.Items.Should().ContainSingle();
        dr.Items.Single().SaleItemId.Should().Be(saleItemId);
        dr.Items.Single().UnitPrice.Should().Be(85m);
        dr.Items.Single().ProductNameSnapshot.Should().Be("Coke 1.5L");
        dr.Items.Single().Quantity.Should().Be(3m);

        var act = () => dr.AddItem(Guid.NewGuid(), "Bad", null, 0m, null);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_defaults_to_pending_status_with_no_audit_fields_set()
    {
        var dr = MakeDelivery();

        dr.Status.Should().Be(FulfillmentStatus.Pending);
        dr.SequenceNumber.Should().Be(1);
        dr.ScheduledDate.Should().Be(new DateOnly(2026, 9, 15));
        dr.CompletedAtUtc.Should().BeNull();
        dr.CompletedByUserId.Should().BeNull();
        dr.CancelledAtUtc.Should().BeNull();
        dr.CancelledByUserId.Should().BeNull();
        dr.CancellationReason.Should().BeNull();
    }

    [Fact]
    public void Create_rejects_a_sequence_number_below_one()
    {
        var act = () => DeliveryReceipt.CreateDelivery(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
            sequenceNumber: 0, scheduledDate: new DateOnly(2026, 9, 15),
            "Juan", "123 Ayala Ave", null, null, Guid.NewGuid(), "x");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MarkDelivered_transitions_pending_to_delivered_and_stamps_audit_fields()
    {
        var dr = MakeDelivery();
        var deliveredBy = Guid.NewGuid();
        var deliveredAt = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

        dr.MarkDelivered(deliveredBy, deliveredAt);

        dr.Status.Should().Be(FulfillmentStatus.Completed);
        dr.CompletedByUserId.Should().Be(deliveredBy);
        dr.CompletedAtUtc.Should().Be(deliveredAt);
    }

    [Fact]
    public void MarkDelivered_twice_throws()
    {
        var dr = MakeDelivery();
        dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_transitions_pending_to_cancelled_and_stamps_audit_fields()
    {
        var dr = MakeDelivery();
        var cancelledBy = Guid.NewGuid();
        var cancelledAt = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc);

        dr.Cancel(cancelledBy, "  Customer rescheduled  ", CancellationDisposition.DeliverLater, cancelledAt);

        dr.Status.Should().Be(FulfillmentStatus.Cancelled);
        dr.CancelledByUserId.Should().Be(cancelledBy);
        dr.CancelledAtUtc.Should().Be(cancelledAt);
        dr.CancellationReason.Should().Be("Customer rescheduled");
    }

    [Fact]
    public void Cancel_requires_a_non_blank_reason()
    {
        var dr = MakeDelivery();

        var act = () => dr.Cancel(Guid.NewGuid(), "   ", CancellationDisposition.DeliverLater, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cancel_after_delivered_throws()
    {
        var dr = MakeDelivery();
        dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => dr.Cancel(Guid.NewGuid(), "Too late", CancellationDisposition.DeliverLater, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkDelivered_after_cancelled_throws()
    {
        var dr = MakeDelivery();
        dr.Cancel(Guid.NewGuid(), "Changed mind", CancellationDisposition.DeliverLater, DateTime.UtcNow);

        var act = () => dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_twice_throws()
    {
        var dr = MakeDelivery();
        dr.Cancel(Guid.NewGuid(), "First reason", CancellationDisposition.DeliverLater, DateTime.UtcNow);

        var act = () => dr.Cancel(Guid.NewGuid(), "Second reason", CancellationDisposition.DeliverLater, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateDelivery_is_a_pending_delivery_with_an_address()
    {
        var dr = MakeDelivery();

        dr.Method.Should().Be(FulfillmentMethod.Delivery);
        dr.Status.Should().Be(FulfillmentStatus.Pending);
        dr.DeliveryAddress.Should().Be("123 Ayala Ave, Makati");
        dr.CancellationDisposition.Should().BeNull();
    }

    [Fact]
    public void CreatePickup_is_a_pending_pickup_with_no_address()
    {
        var pickup = MakePickup();

        pickup.Method.Should().Be(FulfillmentMethod.Pickup);
        pickup.Status.Should().Be(FulfillmentStatus.Pending);
        pickup.DeliveryAddress.Should().BeNull();
        pickup.RecipientName.Should().Be("Juan Dela Cruz");
        pickup.ContactNumber.Should().Be("0917 111 2222");
    }

    [Fact]
    public void CreateDelivery_requires_an_address()
    {
        var act = () => DeliveryReceipt.CreateDelivery(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
            sequenceNumber: 1, scheduledDate: new DateOnly(2026, 9, 15),
            recipientName: "Juan", deliveryAddress: "   ", contactNumber: null, notes: null,
            preparedByUserId: Guid.NewGuid(), preparedByNameSnapshot: "x");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkClaimed_completes_a_pending_pickup_and_stamps_audit_fields()
    {
        var pickup = MakePickup();
        var by = Guid.NewGuid();
        var at = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

        pickup.MarkClaimed(by, at);

        pickup.Status.Should().Be(FulfillmentStatus.Completed);
        pickup.CompletedAtUtc.Should().Be(at);
        pickup.CompletedByUserId.Should().Be(by);
    }

    [Fact]
    public void MarkClaimed_on_a_delivery_throws()
    {
        var dr = MakeDelivery();

        var act = () => dr.MarkClaimed(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkDelivered_on_a_pickup_throws()
    {
        var pickup = MakePickup();

        var act = () => pickup.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_records_its_disposition()
    {
        var dr = MakeDelivery();

        dr.Cancel(Guid.NewGuid(), "Customer rescheduled", CancellationDisposition.DeliverLater, DateTime.UtcNow);

        dr.Status.Should().Be(FulfillmentStatus.Cancelled);
        dr.CancellationDisposition.Should().Be(CancellationDisposition.DeliverLater);
    }

    [Fact]
    public void Cancel_rejects_a_disposition_that_does_not_match_the_method()
    {
        var dr = MakeDelivery();
        var pickup = MakePickup();

        var deliveryWithPickupDisposition =
            () => dr.Cancel(Guid.NewGuid(), "x", CancellationDisposition.PickupLater, DateTime.UtcNow);
        var pickupWithDeliveryDisposition =
            () => pickup.Cancel(Guid.NewGuid(), "x", CancellationDisposition.DeliverLater, DateTime.UtcNow);
        var pickupPickedUpInstead =
            () => pickup.Cancel(Guid.NewGuid(), "x", CancellationDisposition.CustomerPickedUpInstead, DateTime.UtcNow);

        deliveryWithPickupDisposition.Should().Throw<InvalidOperationException>();
        pickupWithDeliveryDisposition.Should().Throw<InvalidOperationException>();
        pickupPickedUpInstead.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkClaimed_twice_throws()
    {
        var pickup = MakePickup();
        pickup.MarkClaimed(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => pickup.MarkClaimed(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
}
