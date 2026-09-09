using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Reports;

namespace Negosio.UnitTests.Reports;

/// <summary>A fixed-instant clock — the whole point of these tests is deterministic "now".</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

public class ReportPeriodResolverTests
{
    /// <summary>2026-09-09 01:30 UTC = 2026-09-09 09:30 in Manila (UTC+8) — safely mid-morning
    /// local, so no boundary ambiguity from the offset itself.</summary>
    private static ReportPeriodResolver ResolverAt(DateTimeOffset utcNow) =>
        new(new FixedTimeProvider(utcNow));

    [Fact]
    public void Today_is_the_business_local_calendar_day_not_the_utc_one()
    {
        // 2026-09-09 20:30 UTC = 2026-09-10 04:30 in Manila — UTC and PH-local disagree on "today".
        var resolver = ResolverAt(new DateTimeOffset(2026, 9, 9, 20, 30, 0, TimeSpan.Zero));

        var range = resolver.Resolve(ReportPeriod.Today, null, null);

        // PH-local Sep 10 00:00 == UTC Sep 9 16:00.
        range.FromUtc.Should().Be(new DateTime(2026, 9, 9, 16, 0, 0, DateTimeKind.Utc));
        range.ToUtc.Should().Be(new DateTime(2026, 9, 10, 16, 0, 0, DateTimeKind.Utc));
        range.Hourly.Should().BeTrue();
    }

    [Fact]
    public void Yesterday_is_one_full_business_local_day_before_today()
    {
        var resolver = ResolverAt(new DateTimeOffset(2026, 9, 9, 1, 30, 0, TimeSpan.Zero)); // PH: Sep 9, 09:30

        var range = resolver.Resolve(ReportPeriod.Yesterday, null, null);

        range.FromUtc.Should().Be(new DateTime(2026, 9, 7, 16, 0, 0, DateTimeKind.Utc)); // PH Sep 8 00:00
        range.ToUtc.Should().Be(new DateTime(2026, 9, 8, 16, 0, 0, DateTimeKind.Utc));   // PH Sep 9 00:00
        range.Hourly.Should().BeTrue();
    }

    [Fact]
    public void Last_7_days_spans_seven_calendar_days_including_today()
    {
        var resolver = ResolverAt(new DateTimeOffset(2026, 9, 9, 1, 30, 0, TimeSpan.Zero)); // PH: Sep 9

        var range = resolver.Resolve(ReportPeriod.Last7Days, null, null);

        (range.ToUtc - range.FromUtc).Should().Be(TimeSpan.FromDays(7));
        range.Hourly.Should().BeFalse();
        // Previous period is the 7 days immediately before this one, same length.
        range.PreviousToUtc.Should().Be(range.FromUtc);
        (range.PreviousToUtc - range.PreviousFromUtc).Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void This_month_previous_period_is_the_same_elapsed_days_immediately_before_it()
    {
        // PH "today" = Sep 9 -> This month = Sep 1 through Sep 9 inclusive = 9 days.
        var resolver = ResolverAt(new DateTimeOffset(2026, 9, 9, 1, 30, 0, TimeSpan.Zero));

        var range = resolver.Resolve(ReportPeriod.ThisMonth, null, null);

        (range.ToUtc - range.FromUtc).Should().Be(TimeSpan.FromDays(9));
        range.PreviousToUtc.Should().Be(range.FromUtc);
        (range.PreviousToUtc - range.PreviousFromUtc).Should().Be(TimeSpan.FromDays(9));
    }

    [Fact]
    public void Custom_range_previous_period_matches_the_documented_example()
    {
        var resolver = ResolverAt(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

        // Sep 1 - Sep 10 (inclusive, 10 days) -> previous should be Aug 22 - Aug 31 (10 days).
        var range = resolver.Resolve(ReportPeriod.Custom, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 10));

        range.FromUtc.Should().Be(new DateTime(2026, 8, 31, 16, 0, 0, DateTimeKind.Utc)); // PH Sep 1 00:00
        range.ToUtc.Should().Be(new DateTime(2026, 9, 10, 16, 0, 0, DateTimeKind.Utc));   // PH Sep 11 00:00 (exclusive)
        range.PreviousFromUtc.Should().Be(new DateTime(2026, 8, 21, 16, 0, 0, DateTimeKind.Utc)); // PH Aug 22 00:00
        range.PreviousToUtc.Should().Be(range.FromUtc); // PH Sep 1 00:00 (exclusive) == PH Aug 31 end-of-day
    }

    [Fact]
    public void Custom_single_day_range_is_hourly()
    {
        var resolver = ResolverAt(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

        var range = resolver.Resolve(ReportPeriod.Custom, new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 5));

        range.Hourly.Should().BeTrue();
    }

    [Fact]
    public void Custom_range_rejects_toDate_before_fromDate()
    {
        var resolver = ResolverAt(DateTimeOffset.UtcNow);

        var act = () => resolver.Resolve(ReportPeriod.Custom, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 1));

        act.Should().Throw<BusinessRuleException>();
    }

    [Fact]
    public void Custom_range_requires_both_dates()
    {
        var resolver = ResolverAt(DateTimeOffset.UtcNow);

        var act = () => resolver.Resolve(ReportPeriod.Custom, new DateOnly(2026, 9, 10), null);

        act.Should().Throw<BusinessRuleException>();
    }
}
