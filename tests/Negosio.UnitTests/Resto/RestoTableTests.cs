// tests/Negosio.UnitTests/Resto/RestoTableTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoTableTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();

    [Fact]
    public void Create_sets_the_expected_fields_and_starts_active()
    {
        var table = RestoTable.Create(TenantId, BranchId, "Table 5");

        table.TenantId.Should().Be(TenantId);
        table.BranchId.Should().Be(BranchId);
        table.Name.Should().Be("Table 5");
        table.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_a_blank_name()
    {
        var act = () => RestoTable.Create(TenantId, BranchId, "");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Disable_then_Enable_round_trips_IsActive()
    {
        var table = RestoTable.Create(TenantId, BranchId, "Patio 2");

        table.Disable();
        table.IsActive.Should().BeFalse();

        table.Enable();
        table.IsActive.Should().BeTrue();
    }
}
