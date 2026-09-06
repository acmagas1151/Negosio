using FluentValidation;

namespace Negosio.Application.Pos;

public sealed class CancelTransactionAuthorizationRequestValidator : AbstractValidator<CancelTransactionAuthorizationRequest>
{
    public CancelTransactionAuthorizationRequestValidator()
    {
        When(x => x.Approval is not null, () =>
        {
            RuleFor(x => x.Approval!.ApproverEmail).NotEmpty().EmailAddress();
            RuleFor(x => x.Approval!.ApproverPassword).NotEmpty();
        });
    }
}
