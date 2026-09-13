using FluentValidation;
using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

public sealed class FulfillmentItemInputValidator : AbstractValidator<FulfillmentItemInput>
{
    public FulfillmentItemInputValidator()
    {
        RuleFor(x => x.SaleItemId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0m).WithMessage("Quantity must be greater than zero.");
    }
}

public sealed class CreateDeliveryReceiptRequestValidator : AbstractValidator<CreateDeliveryReceiptRequest>
{
    public CreateDeliveryReceiptRequestValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().WithMessage("Recipient name is required.").MaximumLength(120);
        RuleFor(x => x.DeliveryAddress).NotEmpty().WithMessage("Recipient address is required.").MaximumLength(300);
        RuleFor(x => x.ContactNumber).MaximumLength(40).When(x => x.ContactNumber != null);
        RuleFor(x => x.Notes).MaximumLength(1000).When(x => x.Notes != null);

        RuleFor(x => x.Items).NotEmpty().WithMessage("A delivery must include at least one item.");
        RuleForEach(x => x.Items).SetValidator(new FulfillmentItemInputValidator());
        RuleFor(x => x.Items)
            .Must(items => items.Select(i => i.SaleItemId).Distinct().Count() == items.Count)
            .WithMessage("Each sale item may be listed at most once per schedule.")
            .When(x => x.Items.Count > 0);

        // Business-local "today" — see the plan's Global Constraints (ReportPeriodResolver.BusinessOffset).
        // The validator has no access to TimeProvider (FluentValidation validators are singletons resolved
        // once by DI, not per-request), so this only catches an obviously-past date typed against the
        // client's own clock; the service re-checks against the server's business-local date, which is
        // the authoritative check (never trust the frontend/validator's clock alone for this).
        RuleFor(x => x.ScheduledDate).NotEqual(default(DateOnly));
    }
}

public sealed class CreatePickupRequestValidator : AbstractValidator<CreatePickupRequest>
{
    public CreatePickupRequestValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().WithMessage("Recipient name is required.").MaximumLength(120);
        RuleFor(x => x.ContactNumber).MaximumLength(40).When(x => x.ContactNumber != null);
        RuleFor(x => x.Notes).MaximumLength(1000).When(x => x.Notes != null);

        RuleFor(x => x.Items).NotEmpty().WithMessage("A pickup must include at least one item.");
        RuleForEach(x => x.Items).SetValidator(new FulfillmentItemInputValidator());
        RuleFor(x => x.Items)
            .Must(items => items.Select(i => i.SaleItemId).Distinct().Count() == items.Count)
            .WithMessage("Each sale item may be listed at most once per schedule.")
            .When(x => x.Items.Count > 0);

        // Business-local "today" — see the plan's Global Constraints (ReportPeriodResolver.BusinessOffset).
        // The validator has no access to TimeProvider (FluentValidation validators are singletons resolved
        // once by DI, not per-request), so this only catches an obviously-past date typed against the
        // client's own clock; the service re-checks against the server's business-local date, which is
        // the authoritative check (never trust the frontend/validator's clock alone for this).
        RuleFor(x => x.ScheduledDate).NotEqual(default(DateOnly));
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

public sealed class CreatePickupBatchRequestValidator : AbstractValidator<CreatePickupBatchRequest>
{
    public CreatePickupBatchRequestValidator()
    {
        RuleFor(x => x.BatchRequestId).NotEmpty();
        RuleFor(x => x.Schedules).NotEmpty().WithMessage("A batch must include at least one pickup schedule.");
        RuleForEach(x => x.Schedules).SetValidator(new CreatePickupRequestValidator());
    }
}

public sealed class PickupReplacementInputValidator : AbstractValidator<PickupReplacementInput>
{
    public PickupReplacementInputValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().WithMessage("Recipient name is required.").MaximumLength(120);
        RuleFor(x => x.ContactNumber).MaximumLength(40).When(x => x.ContactNumber != null);
        RuleFor(x => x.Notes).MaximumLength(1000).When(x => x.Notes != null);
        RuleFor(x => x.ScheduledDate).NotEqual(default(DateOnly));
    }
}

public sealed class DeliveryReplacementInputValidator : AbstractValidator<DeliveryReplacementInput>
{
    public DeliveryReplacementInputValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().WithMessage("Recipient name is required.").MaximumLength(120);
        RuleFor(x => x.DeliveryAddress).NotEmpty().WithMessage("Recipient address is required.").MaximumLength(300);
        RuleFor(x => x.ContactNumber).MaximumLength(40).When(x => x.ContactNumber != null);
        RuleFor(x => x.Notes).MaximumLength(1000).When(x => x.Notes != null);
        RuleFor(x => x.ScheduledDate).NotEqual(default(DateOnly));
    }
}

public sealed class CancelDeliveryRequestValidator : AbstractValidator<CancelDeliveryRequest>
{
    private static readonly CancellationDisposition[] ValidDispositions =
    [
        CancellationDisposition.DeliverLater,
        CancellationDisposition.ConvertToPickup,
        CancellationDisposition.CustomerPickedUpInstead,
    ];

    public CancelDeliveryRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A cancellation reason is required.").MaximumLength(500);

        RuleFor(x => x.Disposition)
            .Must(d => ValidDispositions.Contains(d))
            .WithMessage("That disposition is not valid for a delivery.");

        RuleFor(x => x.Replacement)
            .NotNull()
            .WithMessage("Replacement details are required for this disposition.")
            .When(x => x.Disposition is CancellationDisposition.ConvertToPickup or CancellationDisposition.CustomerPickedUpInstead);

        RuleFor(x => x.Replacement)
            .Null()
            .WithMessage("Replacement details must not be provided for this disposition.")
            .When(x => x.Disposition == CancellationDisposition.DeliverLater);

        RuleFor(x => x.Replacement!)
            .SetValidator(new PickupReplacementInputValidator())
            .When(x => x.Replacement != null);
    }
}

public sealed class CancelPickupRequestValidator : AbstractValidator<CancelPickupRequest>
{
    private static readonly CancellationDisposition[] ValidDispositions =
    [
        CancellationDisposition.PickupLater,
        CancellationDisposition.ConvertToDelivery,
    ];

    public CancelPickupRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A cancellation reason is required.").MaximumLength(500);

        RuleFor(x => x.Disposition)
            .Must(d => ValidDispositions.Contains(d))
            .WithMessage("That disposition is not valid for a pickup.");

        RuleFor(x => x.Replacement)
            .NotNull()
            .WithMessage("Replacement details are required for this disposition.")
            .When(x => x.Disposition == CancellationDisposition.ConvertToDelivery);

        RuleFor(x => x.Replacement)
            .Null()
            .WithMessage("Replacement details must not be provided for this disposition.")
            .When(x => x.Disposition == CancellationDisposition.PickupLater);

        RuleFor(x => x.Replacement!)
            .SetValidator(new DeliveryReplacementInputValidator())
            .When(x => x.Replacement != null);
    }
}
