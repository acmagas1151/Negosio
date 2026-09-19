using FluentValidation;
using Negosio.Domain.Enums;

namespace Negosio.Application.Pos;

public sealed class CheckoutRequestValidator : AbstractValidator<CheckoutRequest>
{
    public CheckoutRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty().WithMessage("Branch is required.");
        RuleFor(x => x.RegisterSessionId).NotEmpty().WithMessage("Register session is required.");
        RuleFor(x => x.ClientRequestId).NotEmpty().WithMessage("A client request id is required for idempotency.");
        RuleFor(x => x.Items).NotEmpty().WithMessage("A sale must have at least one item.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductVariantId).NotEmpty().WithMessage("Each item needs a product variant.");
            item.RuleFor(i => i.Quantity).GreaterThan(0).WithMessage("Item quantity must be greater than zero.");
        });
        RuleFor(x => x.Payments).NotEmpty().WithMessage("At least one payment is required.");
        RuleFor(x => x.Method).IsInEnum().WithMessage("Fulfillment method must be TakeNow, Delivery, or Pickup.");
        RuleFor(x => x.DeliveryCharge)
            .GreaterThanOrEqualTo(0m).WithMessage("Delivery charge cannot be negative.")
            .Must(v => v == Math.Round(v, 2, MidpointRounding.AwayFromZero))
            .WithMessage("Delivery charge can have at most 2 decimal places.")
            // ApplyConditionTo.CurrentValidator: FluentValidation's default When() behavior applies the
            // condition to every validator earlier in this same RuleFor chain too, which would silently
            // skip the GreaterThanOrEqualTo/Must checks above whenever Method == Delivery. Scoping the
            // condition to just this validator keeps those two checks unconditional.
            .Equal(0m).WithMessage("Delivery charge must be 0 unless the fulfillment method is Delivery.")
                .When(x => x.Method != FulfillmentMethod.Delivery, ApplyConditionTo.CurrentValidator);
    }
}
