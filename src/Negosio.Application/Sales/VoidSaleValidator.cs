using FluentValidation;

namespace Negosio.Application.Sales;

public sealed class VoidSaleRequestValidator : AbstractValidator<VoidSaleRequest>
{
    public VoidSaleRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A void reason is required.").MaximumLength(500);

        When(x => x.Approval is not null, () =>
        {
            RuleFor(x => x.Approval!.ApproverEmail).NotEmpty().EmailAddress();
            RuleFor(x => x.Approval!.ApproverPassword).NotEmpty();
        });
    }
}
