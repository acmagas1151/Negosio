using FluentAssertions;
using Negosio.Application.Resto;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoReconciliationOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        new RestoReconciliationOptions().Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    public void Poll_interval_outside_ten_to_fifteen_seconds_is_rejected(int seconds)
    {
        new RestoReconciliationOptions { PollIntervalSeconds = seconds }.Validate().Should().ContainSingle()
            .Which.Should().Contain("PollIntervalSeconds");
    }

    [Fact]
    public void Each_out_of_range_value_is_reported()
    {
        var errors = new RestoReconciliationOptions
        {
            ReleaseGraceSeconds = 301,
            BatchSize = 0,
            UnacknowledgedAlertMinutes = 121,
            FailureBackoffMaxMinutes = 61,
        }.Validate();

        errors.Should().HaveCount(4);
    }

    [Fact]
    public void Zero_grace_is_allowed()
    {
        new RestoReconciliationOptions { ReleaseGraceSeconds = 0 }.Validate().Should().BeEmpty();
    }
}

public class FailureBackoffTests
{
    private static readonly DateTime Start = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_key_is_skipped_until_its_retry_time_then_becomes_eligible_again()
    {
        var backoff = new FailureBackoff<string>();

        var retryAfter = backoff.RecordFailure("order", Start, maxMinutes: 10);

        retryAfter.Should().Be(Start.AddMinutes(1));
        backoff.IsBackedOff("order", Start.AddSeconds(30)).Should().BeTrue();
        backoff.IsBackedOff("order", Start.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void Repeated_failures_double_the_delay_up_to_the_cap()
    {
        var backoff = new FailureBackoff<string>();
        var now = Start;

        var delays = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var retryAfter = backoff.RecordFailure("tenant", now, maxMinutes: 10);
            delays.Add((retryAfter - now).TotalMinutes);
            now = retryAfter;
        }

        delays.Should().Equal(1, 2, 4, 8, 10, 10);
    }

    [Fact]
    public void Clear_forgets_the_failure_history()
    {
        var backoff = new FailureBackoff<string>();
        backoff.RecordFailure("tenant", Start, maxMinutes: 10);
        backoff.RecordFailure("tenant", Start, maxMinutes: 10);

        backoff.Clear("tenant");

        backoff.IsBackedOff("tenant", Start).Should().BeFalse();
        backoff.RecordFailure("tenant", Start, maxMinutes: 10).Should().Be(Start.AddMinutes(1), "the next failure starts over");
    }

    [Fact]
    public void ActiveKeys_lists_only_keys_still_in_backoff()
    {
        var backoff = new FailureBackoff<string>();
        backoff.RecordFailure("active", Start, maxMinutes: 10);
        backoff.RecordFailure("expired", Start.AddMinutes(-5), maxMinutes: 10);

        backoff.ActiveKeys(Start).Should().Equal("active");
    }
}
