using FluentValidation;

namespace Negosio.Application.Delivery;

public sealed class CreateDeliveryReceiptRequestValidator : AbstractValidator<CreateDeliveryReceiptRequest>
{
    public CreateDeliveryReceiptRequestValidator()
    {
        RuleFor(x => x.RecipientName)
            .NotEmpty()
            .WithMessage("Recipient name is required.")
            .MaximumLength(120);

        RuleFor(x => x.DeliveryAddress)
            .NotEmpty()
            .WithMessage("Recipient address is required.")
            .MaximumLength(300);

        RuleFor(x => x.ContactNumber)
            .MaximumLength(40)
            .When(x => x.ContactNumber != null);

        RuleFor(x => x.DeliveryNotes)
            .MaximumLength(1000)
            .When(x => x.DeliveryNotes != null);

        RuleForEach(x => x.Items)
            .SetValidator(new CreateDeliveryReceiptItemInputValidator())
            .When(x => x.Items != null);
    }
}

public sealed class CreateDeliveryReceiptItemInputValidator : AbstractValidator<CreateDeliveryReceiptItemInput>
{
    public CreateDeliveryReceiptItemInputValidator()
    {
        RuleFor(x => x.Quantity)
            .GreaterThan(0);
    }
}
