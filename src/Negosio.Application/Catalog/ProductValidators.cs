using FluentValidation;

namespace Negosio.Application.Catalog;

public sealed class VariantInputValidator : AbstractValidator<VariantInput>
{
    public VariantInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Variant name is required.").MaximumLength(150);
        RuleFor(x => x.Sku).MaximumLength(64);
        RuleFor(x => x.Barcode).MaximumLength(64);
        RuleFor(x => x.CostPrice).GreaterThanOrEqualTo(0).WithMessage("Cost price cannot be negative.");
        RuleFor(x => x.SellingPrice).GreaterThanOrEqualTo(0).WithMessage("Selling price cannot be negative.");
    }
}

public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithMessage("Category is required.");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Product name is required.").MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Sku).MaximumLength(64);
        RuleFor(x => x.Barcode).MaximumLength(64);
        RuleFor(x => x.CostPrice).GreaterThanOrEqualTo(0).WithMessage("Cost price cannot be negative.");
        RuleFor(x => x.SellingPrice).GreaterThanOrEqualTo(0).WithMessage("Selling price cannot be negative.");
        RuleForEach(x => x.Variants).SetValidator(new VariantInputValidator());
    }
}

public sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithMessage("Category is required.");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Product name is required.").MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Sku).MaximumLength(64);
        RuleFor(x => x.Barcode).MaximumLength(64);
        RuleFor(x => x.CostPrice).GreaterThanOrEqualTo(0).WithMessage("Cost price cannot be negative.");
        RuleFor(x => x.SellingPrice).GreaterThanOrEqualTo(0).WithMessage("Selling price cannot be negative.");
    }
}
