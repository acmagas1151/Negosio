using System.Text.RegularExpressions;
using FluentValidation;

namespace Negosio.Application.Auth;

public sealed partial class RegisterRequestValidator : AbstractValidator<RegisterRequest>
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
            RuleFor(x => x.Owner.Password)
                .NotEmpty().WithMessage("Owner password is required.")
                .MinimumLength(8).WithMessage("Password must be at least 8 characters long.")
                .MaximumLength(128)
                .Must(HasRequiredComplexity)
                .WithMessage("Password must contain an uppercase letter, a lowercase letter, a digit, and a special character.");
        });
    }

    private static bool HasRequiredComplexity(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        return UppercaseRegex().IsMatch(password)
            && LowercaseRegex().IsMatch(password)
            && DigitRegex().IsMatch(password)
            && SpecialCharRegex().IsMatch(password);
    }

    [GeneratedRegex("[A-Z]")]
    private static partial Regex UppercaseRegex();

    [GeneratedRegex("[a-z]")]
    private static partial Regex LowercaseRegex();

    [GeneratedRegex("[0-9]")]
    private static partial Regex DigitRegex();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex SpecialCharRegex();
}
