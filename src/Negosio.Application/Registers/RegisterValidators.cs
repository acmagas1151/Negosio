using FluentValidation;

namespace Negosio.Application.Registers;

public sealed class CreateRegisterRequestValidator : AbstractValidator<CreateRegisterRequest>
{
    public CreateRegisterRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty().WithMessage("Branch is required.");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Register name is required.").MaximumLength(100);
        RuleFor(x => x.Code).NotEmpty().WithMessage("Register code is required.").MaximumLength(20);
    }
}

public sealed class UpdateRegisterRequestValidator : AbstractValidator<UpdateRegisterRequest>
{
    public UpdateRegisterRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Register name is required.").MaximumLength(100);
        RuleFor(x => x.Code).NotEmpty().WithMessage("Register code is required.").MaximumLength(20);
    }
}

public sealed class OpenRegisterSessionRequestValidator : AbstractValidator<OpenRegisterSessionRequest>
{
    public OpenRegisterSessionRequestValidator()
    {
        RuleFor(x => x.RegisterId).NotEmpty().WithMessage("Register is required.");
        RuleFor(x => x.OpeningCash).GreaterThanOrEqualTo(0).WithMessage("Opening cash cannot be negative.");
    }
}

public sealed class CloseRegisterSessionRequestValidator : AbstractValidator<CloseRegisterSessionRequest>
{
    public CloseRegisterSessionRequestValidator()
    {
        RuleFor(x => x.ClosingCash).GreaterThanOrEqualTo(0).WithMessage("Closing cash cannot be negative.");
    }
}
