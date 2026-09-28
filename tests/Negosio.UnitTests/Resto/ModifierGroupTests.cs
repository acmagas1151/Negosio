using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class ModifierGroupTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void Create_sets_the_expected_fields_and_starts_active_with_no_options()
    {
        var group = ModifierGroup.Create(TenantId, "Add-ons", ModifierSelectionType.Multiple);

        group.TenantId.Should().Be(TenantId);
        group.Name.Should().Be("Add-ons");
        group.SelectionType.Should().Be(ModifierSelectionType.Multiple);
        group.IsActive.Should().BeTrue();
        group.Options.Should().BeEmpty();
    }

    [Fact]
    public void AddOption_appends_an_active_option_with_the_given_price_delta()
    {
        var group = ModifierGroup.Create(TenantId, "Add-ons", ModifierSelectionType.Multiple);

        var option = group.AddOption("Extra cheese", 20m);

        group.Options.Should().ContainSingle().Which.Should().BeSameAs(option);
        option.Name.Should().Be("Extra cheese");
        option.PriceDelta.Should().Be(20m);
        option.IsActive.Should().BeTrue();
    }

    [Fact]
    public void AddOption_rejects_a_negative_price_delta()
    {
        var group = ModifierGroup.Create(TenantId, "Add-ons", ModifierSelectionType.Single);

        var act = () => group.AddOption("Discount option", -5m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Disable_flips_IsActive_without_touching_existing_options()
    {
        var group = ModifierGroup.Create(TenantId, "Spice Level", ModifierSelectionType.Single);
        var option = group.AddOption("Mild", 0m);

        group.Disable();

        group.IsActive.Should().BeFalse();
        option.IsActive.Should().BeTrue();
    }
}
