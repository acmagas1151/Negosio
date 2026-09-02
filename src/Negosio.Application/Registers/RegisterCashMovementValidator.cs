using FluentValidation;

namespace Negosio.Application.Registers;

public sealed class CreateCashMovementRequestValidator : AbstractValidator<CreateCashMovementRequest>
{
    public CreateCashMovementRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).WithMessage("Amount must be greater than zero.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required.").MaximumLength(500);
    }
}
