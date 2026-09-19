using FluentAssertions;
using FluentValidation.TestHelper;
using Negosio.Application.Delivery;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class CreateDeliveryReceiptRequestValidatorTests
{
    private static CreateDeliveryReceiptRequest Valid() => new(
        ScheduledDate: new DateOnly(2026, 9, 15),
        RecipientName: "John Doe",
        DeliveryAddress: "123 Main St, Apt 4, Springfield",
        ContactNumber: "555-1234",
        Notes: "Ring doorbell twice");

    private readonly CreateDeliveryReceiptRequestValidator _v = new();

    [Fact]
    public void Accepts_a_valid_request()
        => _v.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Accepts_valid_request_with_null_contact_number()
        => _v.TestValidate(Valid() with { ContactNumber = null })
             .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Accepts_valid_request_with_null_delivery_notes()
        => _v.TestValidate(Valid() with { Notes = null })
             .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Rejects_a_default_unset_scheduled_date()
        => _v.TestValidate(Valid() with { ScheduledDate = default })
             .ShouldHaveValidationErrorFor(x => x.ScheduledDate);

    [Fact]
    public void Rejects_blank_recipient_name()
        => _v.TestValidate(Valid() with { RecipientName = "" })
             .ShouldHaveValidationErrorFor(x => x.RecipientName);

    [Fact]
    public void Rejects_empty_recipient_name()
        => _v.TestValidate(Valid() with { RecipientName = "   " })
             .ShouldHaveValidationErrorFor(x => x.RecipientName);

    [Fact]
    public void Rejects_blank_delivery_address()
        => _v.TestValidate(Valid() with { DeliveryAddress = "" })
             .ShouldHaveValidationErrorFor(x => x.DeliveryAddress);

    [Fact]
    public void Rejects_empty_delivery_address()
        => _v.TestValidate(Valid() with { DeliveryAddress = "   " })
             .ShouldHaveValidationErrorFor(x => x.DeliveryAddress);

    [Fact]
    public void Rejects_delivery_address_over_300_chars()
        => _v.TestValidate(Valid() with { DeliveryAddress = new string('x', 301) })
             .ShouldHaveValidationErrorFor(x => x.DeliveryAddress);

    [Fact]
    public void Rejects_contact_number_over_40_chars()
        => _v.TestValidate(Valid() with { ContactNumber = new string('x', 41) })
             .ShouldHaveValidationErrorFor(x => x.ContactNumber);

    [Fact]
    public void Rejects_delivery_notes_over_1000_chars()
        => _v.TestValidate(Valid() with { Notes = new string('x', 1001) })
             .ShouldHaveValidationErrorFor(x => x.Notes);
}
