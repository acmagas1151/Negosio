using FluentAssertions;
using Negosio.Domain.Entities;

namespace Negosio.UnitTests.Domain;

public class CatalogEntityTests
{
    private static VariantSpec Spec(string name = "Default", string? sku = null, decimal cost = 10m, decimal price = 20m) =>
        new(name, sku, null, cost, price);

    [Theory]
    [InlineData("Beverages")]
    [InlineData("beverages")]
    [InlineData("  BEVERAGES  ")]
    public void Category_normalizes_names_consistently(string input)
    {
        Category.Normalize(input).Should().Be("BEVERAGES");
    }

    [Theory]
    [InlineData("Frozen  Goods")]
    [InlineData(" Frozen\tGoods ")]
    [InlineData("frozen goods")]
    public void Category_normalize_collapses_internal_whitespace(string input)
    {
        Category.Normalize(input).Should().Be("FROZEN GOODS");
    }

    [Fact]
    public void Category_Create_keeps_display_casing_but_normalizes()
    {
        var category = Category.Create(Guid.NewGuid(), "  Frozen Goods ", "desc");

        category.Name.Should().Be("Frozen Goods");
        category.NormalizedName.Should().Be("FROZEN GOODS");
        category.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Product_Create_adds_a_hidden_default_variant()
    {
        var product = Product.Create(Guid.NewGuid(), Guid.NewGuid(), "Coke", null, true, Spec(sku: "COKE"));

        product.HasVariants.Should().BeFalse();
        product.Variants.Should().ContainSingle(v => v.IsDefault && v.Sku == "COKE");
    }

    [Fact]
    public void Adding_the_first_variant_promotes_the_default_row()
    {
        var product = Product.Create(Guid.NewGuid(), Guid.NewGuid(), "T-Shirt", null, true, Spec());

        var small = product.AddVariant(new VariantSpec("Small", "TS-S", null, 100m, 199m));

        product.HasVariants.Should().BeTrue();
        product.Variants.Should().ContainSingle();
        small.IsDefault.Should().BeFalse();
        small.Name.Should().Be("Small");
    }

    [Fact]
    public void Second_variant_is_appended()
    {
        var product = Product.Create(Guid.NewGuid(), Guid.NewGuid(), "T-Shirt", null, true, Spec());
        product.AddVariant(new VariantSpec("Small", "TS-S", null, 100m, 199m));
        product.AddVariant(new VariantSpec("Large", "TS-L", null, 120m, 249m));

        product.Variants.Should().HaveCount(2);
        product.Variants.Should().OnlyContain(v => !v.IsDefault);
    }

    [Fact]
    public void Deactivating_the_last_active_variant_throws()
    {
        var product = Product.Create(Guid.NewGuid(), Guid.NewGuid(), "T-Shirt", null, true, Spec());
        var only = product.AddVariant(new VariantSpec("Only", "ONLY", null, 1m, 2m));

        var act = () => product.DeactivateVariant(only.Id);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Negative_prices_are_rejected()
    {
        var act = () => Product.Create(Guid.NewGuid(), Guid.NewGuid(), "X", null, true,
            new VariantSpec("Default", null, null, -1m, 5m));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
