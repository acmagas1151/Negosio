using FluentValidation;

namespace Negosio.Application.Branches;

public sealed class CreateBranchRequestValidator : AbstractValidator<CreateBranchRequest>
{
    public CreateBranchRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Branch name is required.").MaximumLength(150);
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Branch code is required.")
            .MaximumLength(20)
            .Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, numbers and hyphens only.");
        RuleFor(x => x.AddressLine1).NotEmpty().WithMessage("Address line 1 is required.").MaximumLength(200);
        RuleFor(x => x.AddressLine2).MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().WithMessage("City is required.").MaximumLength(100);
        RuleFor(x => x.Province).NotEmpty().WithMessage("Province is required.").MaximumLength(100);
        RuleFor(x => x.PostalCode).MaximumLength(20);
    }
}

public sealed class UpdateBranchRequestValidator : AbstractValidator<UpdateBranchRequest>
{
    public UpdateBranchRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Branch name is required.").MaximumLength(150);
        RuleFor(x => x.AddressLine1).NotEmpty().WithMessage("Address line 1 is required.").MaximumLength(200);
        RuleFor(x => x.AddressLine2).MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().WithMessage("City is required.").MaximumLength(100);
        RuleFor(x => x.Province).NotEmpty().WithMessage("Province is required.").MaximumLength(100);
        RuleFor(x => x.PostalCode).MaximumLength(20);
    }
}
