using FluentValidation;

namespace Negosio.Application.Inventory;

public sealed class AdjustInventoryRequestValidator : AbstractValidator<AdjustInventoryRequest>
{
    public AdjustInventoryRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty().WithMessage("Branch is required.");
        RuleFor(x => x.ProductId).NotEmpty().WithMessage("Product is required.");
        RuleFor(x => x.Adjustment)
            .NotEqual(0m).WithMessage("Adjustment must be a non-zero amount.");
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A reason is required.")
            .MaximumLength(500);
        RuleFor(x => x.ReorderLevel)
            .GreaterThanOrEqualTo(0).When(x => x.ReorderLevel.HasValue)
            .WithMessage("Reorder level cannot be negative.");
    }
}
