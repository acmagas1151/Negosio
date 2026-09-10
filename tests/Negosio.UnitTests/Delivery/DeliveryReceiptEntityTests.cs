using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class DeliveryReceiptEntityTests
{
    private static DeliveryReceipt Make() => DeliveryReceipt.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
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
            "   ", "addr", null, null, Guid.NewGuid(), "x");
        blankName.Should().Throw<ArgumentException>();
        var blankAddr = () => DeliveryReceipt.Create(Guid.NewGuid(), Guid.NewGuid(), null, null,
            "Juan", "   ", null, null, Guid.NewGuid(), "x");
        blankAddr.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddItem_snapshots_line_and_rejects_zero_qty()
    {
        var dr = Make();
        dr.AddItem("Coke 1.5L", null, 3m, 85m);
        dr.Items.Should().ContainSingle();
        dr.Items.Single().UnitPrice.Should().Be(85m);
        dr.Items.Single().ProductNameSnapshot.Should().Be("Coke 1.5L");
        dr.Items.Single().Quantity.Should().Be(3m);

        var act = () => dr.AddItem("Bad", null, 0m, null);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
