using FluentValidation;
using Negosio.Application.Common;

namespace Negosio.Application.Staff;

public sealed class InviteStaffRequestValidator : AbstractValidator<InviteStaffRequest>
{
    public InviteStaffRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Enter a valid email address.")
            .MaximumLength(256);

        RuleFor(x => x.Role).NotEmpty().WithMessage("A role is required.");
    }
}

public sealed class ChangeStaffRoleRequestValidator : AbstractValidator<ChangeStaffRoleRequest>
{
    public ChangeStaffRoleRequestValidator()
    {
        RuleFor(x => x.Role).NotEmpty().WithMessage("A role is required.");
    }
}

public sealed class ChangeStaffBranchRequestValidator : AbstractValidator<ChangeStaffBranchRequest>
{
    public ChangeStaffBranchRequestValidator()
    {
        RuleFor(x => x.BranchId)
            .NotEmpty().WithMessage("A branch is required.")
            .Must(v => Guid.TryParse(v, out _)).WithMessage("Enter a valid branch.");
    }
}

public sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().WithMessage("First name is required.").MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().WithMessage("Last name is required.").MaximumLength(100);
        RuleFor(x => x.Password).Password();
    }
}
