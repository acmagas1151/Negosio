using FluentValidation;

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
    }
}
