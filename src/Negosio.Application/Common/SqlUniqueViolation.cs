using Microsoft.EntityFrameworkCore;

namespace Negosio.Application.Common;

/// <summary>
/// Detects SQL Server unique-constraint / unique-index violations on a <see cref="DbUpdateException"/>
/// without taking a hard dependency on <c>Microsoft.Data.SqlClient</c> (checked by error number via
/// reflection, exactly like the Phase 1 helper in <c>AuthService</c>). Also surfaces the offending
/// index name so a service can map it to a specific <see cref="ErrorCodes"/> value.
/// </summary>
public static class SqlUniqueViolation
{
    // 2601 = unique index violation, 2627 = unique constraint (PK/UNIQUE) violation.
    private static readonly int[] UniqueViolationNumbers = [2601, 2627];

    public static bool Is(DbUpdateException exception) => TryGetConstraintName(exception, out _);

    public static bool TryGetConstraintName(DbUpdateException exception, out string? constraintName)
    {
        constraintName = null;

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            var number = inner.GetType().GetProperty("Number")?.GetValue(inner) as int?;
            if (number is null || Array.IndexOf(UniqueViolationNumbers, number.Value) < 0)
            {
                continue;
            }

            constraintName = ExtractIndexName(inner.Message);
            return true;
        }

        return false;
    }

    /// <summary>
    /// SQL Server phrasing: "...unique index 'IX_ProductVariants_TenantId_Sku'..." or
    /// "...UNIQUE KEY constraint 'IX_...'. Cannot insert duplicate key...".
    /// </summary>
    private static string? ExtractIndexName(string message)
    {
        var start = message.IndexOf('\'');
        if (start < 0)
        {
            return null;
        }

        var end = message.IndexOf('\'', start + 1);
        return end < 0 ? null : message[(start + 1)..end];
    }
}
