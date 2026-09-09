using FluentAssertions;
using FluentValidation.TestHelper;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class UpdateReceiptSettingsRequestValidatorTests
{
    private static UpdateReceiptSettingsRequest Valid() => new(
        ReceiptWidth.Mm80, "Header", "Footer", true, true, true, true, true,
        "DHeader", "DFooter", true, true, true, true);

    private readonly UpdateReceiptSettingsRequestValidator _v = new();

    [Fact]
    public void Accepts_a_valid_request() => _v.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Rejects_header_over_500_chars()
        => _v.TestValidate(Valid() with { SalesHeaderText = new string('x', 501) })
             .ShouldHaveValidationErrorFor(x => x.SalesHeaderText);

    [Fact]
    public void Rejects_footer_with_too_many_lines()
        => _v.TestValidate(Valid() with { SalesFooterText = "a\nb\nc\nd\ne\nf\ng" })
             .ShouldHaveValidationErrorFor(x => x.SalesFooterText);

    [Fact]
    public void Rejects_undefined_width()
        => _v.TestValidate(Valid() with { Width = (ReceiptWidth)9 })
             .ShouldHaveValidationErrorFor(x => x.Width);
}
