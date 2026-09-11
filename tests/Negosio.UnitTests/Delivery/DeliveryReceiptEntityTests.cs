using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class DeliveryReceiptEntityTests
{
    private static DeliveryReceipt Make() => DeliveryReceipt.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
        sequenceNumber: 1, scheduledDeliveryDate: new DateOnly(2026, 9, 15),
        "  Juan Dela Cruz ", " 123 Ayala Ave, Makati ", " 0917 111 2222 ", "  Leave at guardhouse ",
        Guid.NewGuid(), " Cashier One ");

    [Fact]
    public void Create_trims_and_keeps_snapshot_fields()
    {
        var dr = Make();
        dr.RecipientName.Should().Be("Juan Dela Cruz");
        dr.DeliveryAddress.Should().Be("123 Ayala Ave, Makati");
        dr.ContactNumber.Should().Be("0917 111 2222");
        dr.RelatedSaleNumber.Should().Be("0000042");
        dr.PreparedByNameSnapshot.Should().Be("Cashier One");
    }

    [Fact]
    public void Create_requires_recipient_and_address()
    {
        var blankName = () => DeliveryReceipt.Create(Guid.NewGuid(), Guid.NewGuid(), null, null,
            sequenceNumber: 1, scheduledDeliveryDate: new DateOnly(2026, 9, 15),
            "   ", "addr", null, null, Guid.NewGuid(), "x");
        blankName.Should().Throw<ArgumentException>();
        var blankAddr = () => DeliveryReceipt.Create(Guid.NewGuid(), Guid.NewGuid(), null, null,
            sequenceNumber: 1, scheduledDeliveryDate: new DateOnly(2026, 9, 15),
            "Juan", "   ", null, null, Guid.NewGuid(), "x");
        blankAddr.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddItem_snapshots_line_and_rejects_zero_qty()
    {
        var dr = Make();
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
        var dr = Make();

        dr.Status.Should().Be(DeliveryStatus.Pending);
        dr.SequenceNumber.Should().Be(1);
        dr.ScheduledDeliveryDate.Should().Be(new DateOnly(2026, 9, 15));
        dr.DeliveredAtUtc.Should().BeNull();
        dr.DeliveredByUserId.Should().BeNull();
        dr.CancelledAtUtc.Should().BeNull();
        dr.CancelledByUserId.Should().BeNull();
        dr.CancellationReason.Should().BeNull();
    }

    [Fact]
    public void Create_rejects_a_sequence_number_below_one()
    {
        var act = () => DeliveryReceipt.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
            sequenceNumber: 0, scheduledDeliveryDate: new DateOnly(2026, 9, 15),
            "Juan", "123 Ayala Ave", null, null, Guid.NewGuid(), "x");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MarkDelivered_transitions_pending_to_delivered_and_stamps_audit_fields()
    {
        var dr = Make();
        var deliveredBy = Guid.NewGuid();
        var deliveredAt = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

        dr.MarkDelivered(deliveredBy, deliveredAt);

        dr.Status.Should().Be(DeliveryStatus.Delivered);
        dr.DeliveredByUserId.Should().Be(deliveredBy);
        dr.DeliveredAtUtc.Should().Be(deliveredAt);
    }

    [Fact]
    public void MarkDelivered_twice_throws()
    {
        var dr = Make();
        dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_transitions_pending_to_cancelled_and_stamps_audit_fields()
    {
        var dr = Make();
        var cancelledBy = Guid.NewGuid();
        var cancelledAt = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc);

        dr.Cancel(cancelledBy, "  Customer rescheduled  ", cancelledAt);

        dr.Status.Should().Be(DeliveryStatus.Cancelled);
        dr.CancelledByUserId.Should().Be(cancelledBy);
        dr.CancelledAtUtc.Should().Be(cancelledAt);
        dr.CancellationReason.Should().Be("Customer rescheduled");
    }

    [Fact]
    public void Cancel_requires_a_non_blank_reason()
    {
        var dr = Make();

        var act = () => dr.Cancel(Guid.NewGuid(), "   ", DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cancel_after_delivered_throws()
    {
        var dr = Make();
        dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => dr.Cancel(Guid.NewGuid(), "Too late", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkDelivered_after_cancelled_throws()
    {
        var dr = Make();
        dr.Cancel(Guid.NewGuid(), "Changed mind", DateTime.UtcNow);

        var act = () => dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_twice_throws()
    {
        var dr = Make();
        dr.Cancel(Guid.NewGuid(), "First reason", DateTime.UtcNow);

        var act = () => dr.Cancel(Guid.NewGuid(), "Second reason", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
}
