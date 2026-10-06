using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Pos;

public sealed record ResolvedPayment(PaymentMethod Method, decimal Amount, decimal? ReceivedAmount, decimal? ChangeAmount, string? ReferenceNumber);

/// <summary>
/// Allocates submitted payments against a grand total: cash is applied up to the outstanding amount
/// with change returned, non-cash is clamped to the outstanding amount, and any remainder throws.
/// Shared by Retail checkout and RestoPOS settlement.
/// </summary>
public static class PaymentResolver
{
    public static List<ResolvedPayment> Resolve(IReadOnlyList<CheckoutPaymentInput> inputs, decimal grandTotal)
    {
        var outstanding = grandTotal;
        var result = new List<ResolvedPayment>(inputs.Count);

        foreach (var input in inputs)
        {
            if (input.Method == PaymentMethod.Cash)
            {
                var received = input.ReceivedAmount ?? input.Amount
                    ?? throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A cash payment needs the amount received.");
                if (received <= 0m)
                {
                    throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A cash payment must be greater than zero.");
                }

                var applied = Money.Round(Math.Min(received, Math.Max(outstanding, 0m)));
                outstanding -= applied;
                result.Add(new ResolvedPayment(PaymentMethod.Cash, applied, Money.Round(received), Money.Round(received - applied), input.ReferenceNumber));
            }
            else
            {
                var amount = input.Amount ?? input.ReceivedAmount
                    ?? throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A non-cash payment needs an amount.");
                if (amount <= 0m)
                {
                    throw new BusinessRuleException(ErrorCodes.InvalidPayment, "A payment must be greater than zero.");
                }

                var applied = Money.Round(Math.Min(amount, Math.Max(outstanding, 0m)));
                outstanding -= applied;
                result.Add(new ResolvedPayment(input.Method, applied, null, null, input.ReferenceNumber));
            }
        }

        if (Money.Round(outstanding) > 0m)
        {
            throw new BusinessRuleException(ErrorCodes.PaymentInsufficient, "The payments do not cover the sale total.");
        }

        return result;
    }
}
