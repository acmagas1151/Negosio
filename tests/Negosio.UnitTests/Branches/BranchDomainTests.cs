using FluentAssertions;
using Negosio.Domain.Entities;

namespace Negosio.UnitTests.Branches;

public class BranchDomainTests
{
    private static Branch NewBranch() =>
        Branch.Create(Guid.NewGuid(), "Main", "main", "L1", null, "City", "Province", "1000");

    [Fact]
    public void UpdateDetails_trims_and_keeps_the_code()
    {
        var b = NewBranch();
        var before = b.UpdatedAtUtc;

        b.UpdateDetails("  BGC Hub ", " New L1 ", "  ", " Taguig ", " Metro Manila ", "  ");

        b.Name.Should().Be("BGC Hub");
        b.AddressLine1.Should().Be("New L1");
        b.AddressLine2.Should().BeNull();
        b.City.Should().Be("Taguig");
        b.Province.Should().Be("Metro Manila");
        b.PostalCode.Should().BeNull();
        b.Code.Should().Be("MAIN"); // unchanged
        b.UpdatedAtUtc.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void UpdateDetails_rejects_a_blank_name()
    {
        var b = NewBranch();
        var act = () => b.UpdateDetails(" ", "L1", null, "City", "Province", null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Deactivate_then_Reactivate_toggles_IsActive_and_touches()
    {
        var b = NewBranch();
        b.IsActive.Should().BeTrue();

        b.Deactivate();
        b.IsActive.Should().BeFalse();

        b.Reactivate();
        b.IsActive.Should().BeTrue();
    }
}
