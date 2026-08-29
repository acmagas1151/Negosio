using FluentValidation;

namespace Negosio.Application.Common;

/// <summary>
/// Runs a FluentValidation validator and, on failure, throws a <see cref="ValidationAppException"/>
/// whose keys are camelCased (e.g. "Owner.Email" -> "owner.email") so the client-side field mapping
/// stays predictable. Mirrors the private helper in <c>AuthService</c>; that one is left untouched.
/// </summary>
public static class ValidationExtensions
{
    public static async Task ValidateAndThrowAppAsync<T>(
        this IValidator<T> validator,
        T instance,
        CancellationToken cancellationToken = default)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken);
        if (result.IsValid)
        {
            return;
        }

        var errors = result.Errors
            .GroupBy(e => ToCamelCase(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        throw new ValidationAppException(errors);
    }

    private static string ToCamelCase(string propertyName)
    {
        var segments = propertyName.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0)
            {
                segments[i] = char.ToLowerInvariant(segments[i][0]) + segments[i][1..];
            }
        }

        return string.Join('.', segments);
    }
}
