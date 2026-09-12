using FluentValidation;

namespace Negosio.Application.Delivery;

public sealed class CreateDeliveryReceiptItemInputValidator : AbstractValidator<CreateDeliveryReceiptItemInput>
{
    public CreateDeliveryReceiptItemInputValidator()
    {
        RuleFor(x => x.SaleItemId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0m).WithMessage("Delivery quantity must be greater than zero.");
    }
}

public sealed class CreateDeliveryReceiptRequestValidator : AbstractValidator<CreateDeliveryReceiptRequest>
{
    public CreateDeliveryReceiptRequestValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().WithMessage("Recipient name is required.").MaximumLength(120);
        RuleFor(x => x.DeliveryAddress).NotEmpty().WithMessage("Recipient address is required.").MaximumLength(300);
        RuleFor(x => x.ContactNumber).MaximumLength(40).When(x => x.ContactNumber != null);
        RuleFor(x => x.DeliveryNotes).MaximumLength(1000).When(x => x.DeliveryNotes != null);

        RuleFor(x => x.Items).NotEmpty().WithMessage("A delivery must include at least one item.");
        RuleForEach(x => x.Items).SetValidator(new CreateDeliveryReceiptItemInputValidator());
        RuleFor(x => x.Items)
            .Must(items => items.Select(i => i.SaleItemId).Distinct().Count() == items.Count)
            .WithMessage("Each sale item may be listed at most once per delivery.")
            .When(x => x.Items.Count > 0);

        // Business-local "today" — see the plan's Global Constraints (ReportPeriodResolver.BusinessOffset).
        // The validator has no access to TimeProvider (FluentValidation validators are singletons resolved
        // once by DI, not per-request), so this only catches an obviously-past date typed against the
        // client's own clock; the service re-checks against the server's business-local date, which is
        // the authoritative check (never trust the frontend/validator's clock alone for this).
        RuleFor(x => x.ScheduledDeliveryDate).NotEqual(default(DateOnly));
    }
}

public sealed class CreateDeliveryReceiptBatchRequestValidator : AbstractValidator<CreateDeliveryReceiptBatchRequest>
{
    public CreateDeliveryReceiptBatchRequestValidator()
    {
        RuleFor(x => x.BatchRequestId).NotEmpty();
        RuleFor(x => x.Schedules).NotEmpty().WithMessage("A batch must include at least one delivery schedule.");
        RuleForEach(x => x.Schedules).SetValidator(new CreateDeliveryReceiptRequestValidator());
    }
}

public sealed class CancelDeliveryReceiptRequestValidator : AbstractValidator<CancelDeliveryReceiptRequest>
{
    public CancelDeliveryReceiptRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A cancellation reason is required.").MaximumLength(500);
    }
}
