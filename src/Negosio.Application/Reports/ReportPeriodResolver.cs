using Negosio.Application.Common;

namespace Negosio.Application.Reports;

/// <summary>
/// Resolves a <see cref="ReportPeriod"/> into concrete UTC instant boundaries, plus the immediately
/// preceding period of equal length for "vs previous" comparisons. This is the single place that
/// knows about the business's local day — every report query goes through it rather than each
/// building its own date arithmetic.
///
/// Negosio currently operates only in the Philippines. Asia/Manila is a fixed UTC+8 offset with no
/// DST, so a hardcoded offset is not an approximation — it is exactly correct today. This is a
/// deliberate, single-tenant-region v1 simplification, not a general timezone system: if Negosio
/// ever supports tenants outside the Philippines, this must become tenant-configurable
/// (e.g. a real <c>TenantProfile.TimeZoneId</c>) instead of a shared constant.
/// </summary>
public sealed class ReportPeriodResolver : IReportPeriodResolver
{
    public static readonly TimeSpan BusinessOffset = TimeSpan.FromHours(8);

    private readonly TimeProvider _timeProvider;

    public ReportPeriodResolver(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ResolvedReportRange Resolve(ReportPeriod period, DateOnly? fromDate, DateOnly? toDate)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var todayLocal = DateOnly.FromDateTime(nowUtc + BusinessOffset);

        // Both ends are business-local calendar dates, inclusive — converted to a half-open UTC
        // instant range ([fromUtc, toUtc)) right before returning.
        var (rangeFrom, rangeToInclusive) = period switch
        {
            ReportPeriod.Today => (todayLocal, todayLocal),
            ReportPeriod.Yesterday => (todayLocal.AddDays(-1), todayLocal.AddDays(-1)),
            ReportPeriod.Last7Days => (todayLocal.AddDays(-6), todayLocal),
            ReportPeriod.Last30Days => (todayLocal.AddDays(-29), todayLocal),
            ReportPeriod.ThisMonth => (new DateOnly(todayLocal.Year, todayLocal.Month, 1), todayLocal),
            ReportPeriod.Custom => ResolveCustom(fromDate, toDate),
            _ => throw new BusinessRuleException(ErrorCodes.ValidationFailed, "Unknown report period.")
        };

        var fromUtc = ToUtc(rangeFrom);
        var toUtc = ToUtc(rangeToInclusive.AddDays(1)); // exclusive upper bound
        var duration = toUtc - fromUtc;

        // "Immediately preceding period of equal length" — the same rule for every preset AND for
        // Custom, so a range like Sep 1–Sep 10 compares against Aug 22–Aug 31, deterministically.
        var previousToUtc = fromUtc;
        var previousFromUtc = fromUtc - duration;

        return new ResolvedReportRange(fromUtc, toUtc, previousFromUtc, previousToUtc, Hourly: rangeFrom == rangeToInclusive);
    }

    private static (DateOnly From, DateOnly ToInclusive) ResolveCustom(DateOnly? fromDate, DateOnly? toDate)
    {
        if (fromDate is null || toDate is null)
        {
            throw new BusinessRuleException(ErrorCodes.ValidationFailed, "A custom report range requires both fromDate and toDate.");
        }

        if (toDate < fromDate)
        {
            throw new BusinessRuleException(ErrorCodes.ValidationFailed, "toDate cannot be before fromDate.");
        }

        return (fromDate.Value, toDate.Value);
    }

    /// <summary>Business-local midnight of <paramref name="localDate"/>, as a UTC instant.</summary>
    private static DateTime ToUtc(DateOnly localDate) =>
        DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue) - BusinessOffset, DateTimeKind.Utc);
}
