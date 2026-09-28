using FluentAssertions;
using Negosio.Domain.Entities;

namespace Negosio.UnitTests.Branches;

public class BranchTests
{
    [Fact]
    public void New_branches_default_to_no_Resto_service_types_enabled()
    {
        var branch = Branch.Create(Guid.NewGuid(), "Main", "MAIN", "123 St", null, "City", "Province", null, null);

        branch.SupportsPayAsYouOrder.Should().BeFalse();
        branch.SupportsBillOut.Should().BeFalse();
    }

    [Fact]
    public void ConfigureRestoServiceTypes_sets_both_flags_independently()
    {
        var branch = Branch.Create(Guid.NewGuid(), "Main", "MAIN", "123 St", null, "City", "Province", null, null);

        branch.ConfigureRestoServiceTypes(supportsPayAsYouOrder: true, supportsBillOut: false);

        branch.SupportsPayAsYouOrder.Should().BeTrue();
        branch.SupportsBillOut.Should().BeFalse();
    }
}
