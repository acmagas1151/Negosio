using FluentAssertions;
using FluentValidation.TestHelper;
using Negosio.Application.Delivery;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class CreateDeliveryReceiptRequestValidatorTests
{
    private static CreateDeliveryReceiptRequest Valid() => new(
        ScheduledDeliveryDate: new DateOnly(2026, 9, 15),
        RecipientName: "John Doe",
        DeliveryAddress: "123 Main St, Apt 4, Springfield",
        ContactNumber: "555-1234",
        DeliveryNotes: "Ring doorbell twice",
        Items: new[] { new CreateDeliveryReceiptItemInput(Guid.NewGuid(), 5) });

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
        => _v.TestValidate(Valid() with { DeliveryNotes = null })
             .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Rejects_an_empty_item_list()
        => _v.TestValidate(Valid() with { Items = Array.Empty<CreateDeliveryReceiptItemInput>() })
             .ShouldHaveValidationErrorFor(x => x.Items);

    [Fact]
    public void Rejects_duplicate_sale_item_within_one_delivery()
    {
        var saleItemId = Guid.NewGuid();
        _v.TestValidate(Valid() with
        {
            Items = new[]
            {
                new CreateDeliveryReceiptItemInput(saleItemId, 1m),
                new CreateDeliveryReceiptItemInput(saleItemId, 2m),
            },
        }).ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Rejects_a_default_unset_scheduled_date()
        => _v.TestValidate(Valid() with { ScheduledDeliveryDate = default })
             .ShouldHaveValidationErrorFor(x => x.ScheduledDeliveryDate);

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
        => _v.TestValidate(Valid() with { DeliveryNotes = new string('x', 1001) })
             .ShouldHaveValidationErrorFor(x => x.DeliveryNotes);

    [Fact]
    public void Rejects_item_with_zero_quantity()
        => _v.TestValidate(Valid() with { Items = new[] { new CreateDeliveryReceiptItemInput(Guid.NewGuid(), 0) } })
             .ShouldHaveValidationErrorFor("Items[0].Quantity");

    [Fact]
    public void Rejects_item_with_negative_quantity()
        => _v.TestValidate(Valid() with { Items = new[] { new CreateDeliveryReceiptItemInput(Guid.NewGuid(), -5) } })
             .ShouldHaveValidationErrorFor("Items[0].Quantity");
}
