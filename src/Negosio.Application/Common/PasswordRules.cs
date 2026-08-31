using System.Text.RegularExpressions;
using FluentValidation;

namespace Negosio.Application.Common;

/// <summary>
/// The single password policy for the platform — used by business registration and by staff
/// invitation acceptance so a staff account is never held to a different bar than an owner account.
/// </summary>
public static partial class PasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public const string ComplexityMessage =
        "Password must contain an uppercase letter, a lowercase letter, a digit, and a special character.";

    public static bool MeetsComplexity(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        return UppercaseRegex().IsMatch(password)
            && LowercaseRegex().IsMatch(password)
            && DigitRegex().IsMatch(password)
            && SpecialCharRegex().IsMatch(password);
    }

    /// <summary>Applies the shared password policy to a string rule.</summary>
    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(MinLength).WithMessage($"Password must be at least {MinLength} characters long.")
            .MaximumLength(MaxLength)
            .Must(MeetsComplexity).WithMessage(ComplexityMessage);

    [GeneratedRegex("[A-Z]")]
    private static partial Regex UppercaseRegex();

    [GeneratedRegex("[a-z]")]
    private static partial Regex LowercaseRegex();

    [GeneratedRegex("[0-9]")]
    private static partial Regex DigitRegex();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex SpecialCharRegex();
}
