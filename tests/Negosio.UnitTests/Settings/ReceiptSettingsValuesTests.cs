using FluentAssertions;
using Negosio.Domain.Common;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class ReceiptSettingsValuesTests
{
    [Fact]
    public void HardcodedDefault_reproduces_todays_receipt_behaviour()
    {
        var d = ReceiptSettingsValues.HardcodedDefault;

        d.Width.Should().Be(ReceiptWidth.Mm80);
        d.SalesHeaderText.Should().BeNull();
        d.SalesFooterText.Should().BeNull();
        d.SalesShowBranch.Should().BeTrue();
        d.SalesShowCashier.Should().BeTrue();
        d.SalesShowPaymentMethod.Should().BeTrue();
        d.SalesShowTaxLine.Should().BeTrue();
        d.SalesShowReferenceNumber.Should().BeTrue();
        d.DeliveryShowPrices.Should().BeTrue();
        d.DeliveryShowRelatedSaleNumber.Should().BeTrue();
        d.DeliveryShowContactNumber.Should().BeTrue();
        d.DeliveryShowSignatureFields.Should().BeTrue();
    }
}
