using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoStationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();

    [Fact]
    public void Create_sets_the_expected_fields_and_starts_active()
    {
        var station = RestoStation.Create(TenantId, BranchId, "Kitchen");

        station.TenantId.Should().Be(TenantId);
        station.BranchId.Should().Be(BranchId);
        station.Name.Should().Be("Kitchen");
        station.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_a_blank_name()
    {
        var act = () => RestoStation.Create(TenantId, BranchId, "   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rename_trims_and_updates_the_name()
    {
        var station = RestoStation.Create(TenantId, BranchId, "Kitchen");

        station.Rename("  Hot Kitchen  ");

        station.Name.Should().Be("Hot Kitchen");
    }

    [Fact]
    public void Disable_then_Enable_round_trips_IsActive()
    {
        var station = RestoStation.Create(TenantId, BranchId, "Bar");

        station.Disable();
        station.IsActive.Should().BeFalse();

        station.Enable();
        station.IsActive.Should().BeTrue();
    }
}
