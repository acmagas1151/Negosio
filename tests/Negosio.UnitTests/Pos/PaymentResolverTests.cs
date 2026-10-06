using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Pos;

public class PaymentResolverTests
{
    [Fact]
    public void Cash_overpayment_is_applied_up_to_the_total_and_returns_change()
    {
        var resolved = PaymentResolver.Resolve(
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) }, grandTotal: 450m);

        resolved.Should().ContainSingle();
        resolved[0].Amount.Should().Be(450m);
        resolved[0].ReceivedAmount.Should().Be(500m);
        resolved[0].ChangeAmount.Should().Be(50m);
    }

    [Fact]
    public void Split_tender_clamps_the_second_payment_to_the_outstanding_balance()
    {
        var resolved = PaymentResolver.Resolve(
            new[]
            {
                new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m),
                new CheckoutPaymentInput(PaymentMethod.GCash, Amount: 300m),
            },
            grandTotal: 450m);

        resolved.Should().HaveCount(2);
        resolved[0].Amount.Should().Be(200m);
        resolved[1].Amount.Should().Be(250m);
        resolved[1].ReceivedAmount.Should().BeNull();
    }

    [Fact]
    public void Underpayment_throws_payment_insufficient()
    {
        var act = () => PaymentResolver.Resolve(
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 100m) }, grandTotal: 450m);

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be(ErrorCodes.PaymentInsufficient);
    }

    [Fact]
    public void Zero_cash_payment_is_rejected()
    {
        var act = () => PaymentResolver.Resolve(
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 0m) }, grandTotal: 10m);

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be(ErrorCodes.InvalidPayment);
    }

    [Fact]
    public void Non_cash_payment_without_an_amount_is_rejected()
    {
        var act = () => PaymentResolver.Resolve(
            new[] { new CheckoutPaymentInput(PaymentMethod.GCash) }, grandTotal: 10m);

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be(ErrorCodes.InvalidPayment);
    }
}
