using System.Text.RegularExpressions;
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

    /// <summary>
    /// True when this exception (or any inner) is a SQL Server unique violation. Accepts a raw
    /// <see cref="Exception"/> so callers running raw SQL (e.g. a counter INSERT) can catch it too.
    /// </summary>
    public static bool Is(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var number = current.GetType().GetProperty("Number")?.GetValue(current) as int?;
            if (number is not null && Array.IndexOf(UniqueViolationNumbers, number.Value) >= 0)
            {
                return true;
            }
        }

        return false;
    }

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
    /// SQL Server phrasing: "Cannot insert duplicate key row in object 'dbo.RestoOrders' with unique index
    /// 'IX_RestoOrders_TableId_Open'." The first quoted token is the table, so the index name is read from the
    /// text that introduces it ("unique index" or "UNIQUE KEY constraint").
    /// </summary>
    private static string? ExtractIndexName(string message)
    {
        var match = IndexNamePattern.Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static readonly Regex IndexNamePattern = new(
        @"(?:unique index|UNIQUE KEY constraint) '([^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
