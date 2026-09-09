using FluentAssertions;
using Negosio.Application.Settings;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class ReceiptTextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  hi  ", "hi")]
    [InlineData("a\t\t  b", "a b")]
    [InlineData("line1\n\n\n\nline2", "line1\n\nline2")]
    public void Normalize_trims_collapses_and_nulls(string? input, string? expected)
        => ReceiptText.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_strips_control_chars_but_keeps_newlines()
        => ReceiptText.Normalize("ab\nc").Should().Be("ab\nc");
}
