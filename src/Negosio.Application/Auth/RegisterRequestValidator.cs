using FluentValidation;
using Negosio.Application.Common;

namespace Negosio.Application.Auth;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.BusinessName)
            .NotEmpty().WithMessage("Business name is required.")
            .MaximumLength(200);

        RuleFor(x => x.BusinessType)
            .NotEmpty().WithMessage("Business type is required.");

        RuleFor(x => x.Branch).NotNull().WithMessage("Branch details are required.");
        When(x => x.Branch is not null, () =>
        {
            RuleFor(x => x.Branch.Name).NotEmpty().WithMessage("Branch name is required.").MaximumLength(150);
            RuleFor(x => x.Branch.Code).NotEmpty().WithMessage("Branch code is required.").MaximumLength(20);
            RuleFor(x => x.Branch.AddressLine1).NotEmpty().WithMessage("Address line 1 is required.").MaximumLength(200);
            RuleFor(x => x.Branch.AddressLine2).MaximumLength(200);
            RuleFor(x => x.Branch.City).NotEmpty().WithMessage("City is required.").MaximumLength(100);
            RuleFor(x => x.Branch.Province).NotEmpty().WithMessage("Province is required.").MaximumLength(100);
            RuleFor(x => x.Branch.PostalCode).MaximumLength(20);
        });

        RuleFor(x => x.Owner).NotNull().WithMessage("Owner details are required.");
        When(x => x.Owner is not null, () =>
        {
            RuleFor(x => x.Owner.FirstName).NotEmpty().WithMessage("Owner first name is required.").MaximumLength(100);
            RuleFor(x => x.Owner.LastName).NotEmpty().WithMessage("Owner last name is required.").MaximumLength(100);
            RuleFor(x => x.Owner.Email)
                .NotEmpty().WithMessage("Owner email is required.")
                .EmailAddress().WithMessage("Owner email is not a valid email address.")
                .MaximumLength(256);
            RuleFor(x => x.Owner.Password).Password();
        });
    }
}
