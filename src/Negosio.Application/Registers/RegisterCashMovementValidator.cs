using FluentValidation;

namespace Negosio.Application.Registers;

public sealed class CreateCashMovementRequestValidator : AbstractValidator<CreateCashMovementRequest>
{
    public CreateCashMovementRequestValidator()
    {
        // Without this, an out-of-range integer (e.g. {"type": 99, ...}) deserializes successfully
        // (no allowIntegerValues: false on the enum converter) and would otherwise sail through to
        // RegisterCashMovement.Create, inserting a row that then matches neither CashIn nor CashOut
        // in ReconcileAndCloseAsync's SUM queries — a real, reason-carrying drawer movement silently
        // excluded from expected cash. Reported via the same generic VALIDATION_FAILED path as
        // Amount/Reason below, not a dedicated error code — consistent with how those two are
        // reported, and how this codebase's other input-shape checks (as opposed to business-rule
        // checks) are surfaced.
        RuleFor(x => x.Type).IsInEnum().WithMessage("'{PropertyValue}' is not a valid cash movement type.");
        RuleFor(x => x.Amount).GreaterThan(0m).WithMessage("Amount must be greater than zero.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required.").MaximumLength(500);
    }
}
