using FluentValidation;

namespace Negosio.Application.Sales;

public sealed class CreateReturnRequestValidator : AbstractValidator<CreateReturnRequest>
{
    public CreateReturnRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Select at least one item to return.");
        RuleForEach(x => x.Items).ChildRules(line =>
        {
            line.RuleFor(l => l.SaleItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0).WithMessage("Return quantity must be greater than zero.");
        });
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A return reason is required.").MaximumLength(500);
    }
}
