using FluentValidation;

namespace Negosio.Application.Resto;

public sealed class OpenRestoOrderRequestValidator : AbstractValidator<OpenRestoOrderRequest>
{
    public OpenRestoOrderRequestValidator()
    {
        RuleFor(x => x.ServiceType).IsInEnum();
        RuleFor(x => x.RegisterSessionId).NotEmpty();
        RuleFor(x => x.DisplayLabel).MaximumLength(100);
    }
}

public sealed class RestoStructuralRequestValidator : AbstractValidator<RestoStructuralRequest>
{
    public RestoStructuralRequestValidator()
    {
        RuleFor(x => x.ExpectedRowVersion).NotNull().NotEmpty();
    }
}

public sealed class AddRestoItemRequestValidator : AbstractValidator<AddRestoItemRequest>
{
    public AddRestoItemRequestValidator()
    {
        RuleFor(x => x.ExpectedRowVersion).NotNull().NotEmpty();
        RuleFor(x => x.RoundId).NotEmpty();
        RuleFor(x => x.ProductVariantId).NotEmpty();
        RuleFor(x => x.StationId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0m).WithMessage("Quantity must be greater than zero.");
        RuleFor(x => x.ModifierOptionIds).NotNull();
        RuleFor(x => x.ModifierOptionIds).Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("A modifier option can only be selected once.");
        RuleFor(x => x.ModifierOptionIds).Must(ids => ids.Count <= 20)
            .WithMessage("An item supports at most 20 modifier options.");
        RuleFor(x => x.KitchenNote).MaximumLength(500);
    }
}

public sealed class VoidRestoItemRequestValidator : AbstractValidator<VoidRestoItemRequest>
{
    public VoidRestoItemRequestValidator()
    {
        RuleFor(x => x.ExpectedRowVersion).NotNull().NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class CancelRestoOrderRequestValidator : AbstractValidator<CancelRestoOrderRequest>
{
    public CancelRestoOrderRequestValidator()
    {
        RuleFor(x => x.ExpectedRowVersion).NotNull().NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class SettleRestoOrderRequestValidator : AbstractValidator<SettleRestoOrderRequest>
{
    public SettleRestoOrderRequestValidator()
    {
        RuleFor(x => x.ExpectedRowVersion).NotNull().NotEmpty();
        RuleFor(x => x.SettlementRequestId).NotEmpty();
        RuleFor(x => x.RegisterSessionId).NotEmpty();
        RuleFor(x => x.Payments).NotNull().NotEmpty().WithMessage("At least one payment is required.");
    }
}

public sealed class UnpaidCloseRestoOrderRequestValidator : AbstractValidator<UnpaidCloseRestoOrderRequest>
{
    public UnpaidCloseRestoOrderRequestValidator()
    {
        RuleFor(x => x.ExpectedRowVersion).NotNull().NotEmpty();
        RuleFor(x => x.ClosureRequestId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
