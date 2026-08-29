using FluentAssertions;
using FluentValidation.TestHelper;
using Negosio.Application.Auth;

namespace Negosio.UnitTests.Validation;

public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _validator = new();

    private static RegisterRequest Valid() => new(
        "Bruno's Cafe",
        "FoodAndBeverage",
        new RegisterBranchInput("Main Branch", "MAIN", "Odiongan", null, "Odiongan", "Romblon", "5505"),
        new RegisterOwnerInput("Ace", "Agas", "owner@example.com", "SecurePassword123!"));

    [Fact]
    public void Valid_request_passes()
    {
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("Shrt1!a", false)]        // too short (7 chars)
    [InlineData("alllowercase1!", false)] // no uppercase
    [InlineData("ALLUPPERCASE1!", false)] // no lowercase
    [InlineData("NoDigitsHere!", false)]  // no digit
    [InlineData("NoSpecial123", false)]   // no special char
    [InlineData("SecurePassword123!", true)]
    public void Password_complexity_is_enforced(string password, bool expectedValid)
    {
        var request = Valid() with { Owner = Valid().Owner with { Password = password } };

        var result = _validator.TestValidate(request);

        if (expectedValid)
        {
            result.ShouldNotHaveValidationErrorFor(x => x.Owner.Password);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(x => x.Owner.Password);
        }
    }

    [Fact]
    public void Missing_required_fields_are_reported()
    {
        var request = new RegisterRequest(
            "",
            "",
            new RegisterBranchInput("", "", "", null, "", "", null),
            new RegisterOwnerInput("", "", "not-an-email", ""));

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.BusinessName);
        result.ShouldHaveValidationErrorFor(x => x.BusinessType);
        result.ShouldHaveValidationErrorFor(x => x.Branch.Name);
        result.ShouldHaveValidationErrorFor(x => x.Branch.Code);
        result.ShouldHaveValidationErrorFor(x => x.Owner.FirstName);
        result.ShouldHaveValidationErrorFor(x => x.Owner.LastName);
        result.ShouldHaveValidationErrorFor(x => x.Owner.Email);
    }
}
