using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Catalog;

public sealed class ProductVariantService : IProductVariantService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<VariantInput> _validator;

    public ProductVariantService(ITenantDbContext db, ICurrentUser currentUser, IValidator<VariantInput> validator)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
    }

    public async Task<IReadOnlyList<ProductVariantDto>> ListAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var product = await LoadProductAsync(productId, cancellationToken);
        var canViewCost = CatalogAccess.CanViewCost(_currentUser.Role);

        return product.Variants
            .Where(v => !product.HasVariants || !v.IsDefault)
            .OrderBy(v => v.Name)
            .Select(v => Map(v, canViewCost))
            .ToList();
    }

    public async Task<ProductVariantDto> CreateAsync(Guid productId, VariantInput input, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAppAsync(input, cancellationToken);
        var tenantId = RequireTenant();
        var product = await LoadProductAsync(productId, cancellationToken);

        await GuardCodesAsync(tenantId, input.Sku, input.Barcode, excludeVariantId: null, cancellationToken);

        var variant = product.AddVariant(ToSpec(input));
        await SaveOrTranslateAsync(cancellationToken);

        return Map(variant, CatalogAccess.CanViewCost(_currentUser.Role));
    }

    public async Task<ProductVariantDto> UpdateAsync(Guid productId, Guid variantId, VariantInput input, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAppAsync(input, cancellationToken);
        var tenantId = RequireTenant();
        var product = await LoadProductAsync(productId, cancellationToken);

        var variant = product.Variants.FirstOrDefault(v => v.Id == variantId && !v.IsDefault)
            ?? throw new NotFoundException(ErrorCodes.VariantNotFound, "Variant not found.");

        await GuardCodesAsync(tenantId, input.Sku, input.Barcode, excludeVariantId: variant.Id, cancellationToken);

        product.UpdateVariant(variantId, ToSpec(input));
        await SaveOrTranslateAsync(cancellationToken);

        return Map(variant, CatalogAccess.CanViewCost(_currentUser.Role));
    }

    public async Task DeactivateAsync(Guid productId, Guid variantId, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        var product = await LoadProductAsync(productId, cancellationToken);

        var variant = product.Variants.FirstOrDefault(v => v.Id == variantId && !v.IsDefault)
            ?? throw new NotFoundException(ErrorCodes.VariantNotFound, "Variant not found.");

        if (variant.IsActive && product.Variants.Count(v => v.IsActive) <= 1)
        {
            throw new BusinessRuleException(ErrorCodes.ProductHasVariants, "A product must keep at least one active variant.");
        }

        product.DeactivateVariant(variantId);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Product> LoadProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        return await _db.Products
            .Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.ProductNotFound, "Product not found.");
    }

    private async Task GuardCodesAsync(Guid tenantId, string? sku, string? barcode, Guid? excludeVariantId, CancellationToken cancellationToken)
    {
        var cleanSku = Clean(sku);
        if (cleanSku is not null && await _db.ProductVariants.AnyAsync(
                v => v.TenantId == tenantId && v.Sku == cleanSku && (excludeVariantId == null || v.Id != excludeVariantId),
                cancellationToken))
        {
            throw new ConflictException(ErrorCodes.SkuAlreadyExists, "A product or variant with this SKU already exists.");
        }

        var cleanBarcode = Clean(barcode);
        if (cleanBarcode is not null && await _db.ProductVariants.AnyAsync(
                v => v.TenantId == tenantId && v.Barcode == cleanBarcode && (excludeVariantId == null || v.Id != excludeVariantId),
                cancellationToken))
        {
            throw new ConflictException(ErrorCodes.BarcodeAlreadyExists, "A product or variant with this barcode already exists.");
        }
    }

    private async Task SaveOrTranslateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.TryGetConstraintName(ex, out var name))
        {
            if (name?.Contains("Barcode", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new ConflictException(ErrorCodes.BarcodeAlreadyExists, "A product or variant with this barcode already exists.");
            }

            throw new ConflictException(ErrorCodes.SkuAlreadyExists, "A product or variant with this SKU already exists.");
        }
    }

    private static VariantSpec ToSpec(VariantInput input) =>
        new(input.Name, input.Sku, input.Barcode, input.CostPrice, input.SellingPrice);

    private static ProductVariantDto Map(ProductVariant v, bool canViewCost) => new(
        v.Id, v.Name, v.IsDefault, v.Sku, v.Barcode,
        canViewCost ? v.CostPrice : null, v.SellingPrice, v.IsActive, v.CreatedAtUtc, v.UpdatedAtUtc);

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
