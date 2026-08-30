using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Catalog;

public sealed class ProductService : IProductService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateProductRequest> _createValidator;
    private readonly IValidator<UpdateProductRequest> _updateValidator;

    public ProductService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateProductRequest> createValidator,
        IValidator<UpdateProductRequest> updateValidator)
    {
        _db = db;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    private sealed record ProductRow(
        Guid Id, Guid CategoryId, string CategoryName, string Name, string? Description,
        bool TrackInventory, bool IsActive, bool HasVariants, int VariantCount,
        string? Sku, string? Barcode, decimal? MinCost, decimal? MaxCost,
        decimal MinSelling, decimal MaxSelling, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

    public async Task<PagedResult<ProductDto>> ListAsync(ProductListQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var products = _db.Products.AsNoTracking().Where(p => p.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            products = products.Where(p =>
                p.Name.Contains(term) ||
                p.Variants.Any(v => (v.Sku != null && v.Sku.Contains(term)) ||
                                    (v.Barcode != null && v.Barcode.Contains(term))));
        }

        if (query.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        if (query.IsActive is { } isActive)
        {
            products = products.Where(p => p.IsActive == isActive);
        }

        if (query.TrackInventory is { } trackInventory)
        {
            products = products.Where(p => p.TrackInventory == trackInventory);
        }

        products = ApplySort(products, query.SortBy, query.SortDirection);

        var projected = products.Select(p => new ProductRow(
            p.Id,
            p.CategoryId,
            _db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault() ?? string.Empty,
            p.Name,
            p.Description,
            p.TrackInventory,
            p.IsActive,
            p.HasVariants,
            p.Variants.Count(v => !v.IsDefault),
            p.HasVariants ? null : p.Variants.Where(v => v.IsDefault).Select(v => v.Sku).FirstOrDefault(),
            p.HasVariants ? null : p.Variants.Where(v => v.IsDefault).Select(v => v.Barcode).FirstOrDefault(),
            p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.CostPrice),
            p.Variants.Where(v => v.IsActive).Max(v => (decimal?)v.CostPrice),
            p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.SellingPrice) ?? 0m,
            p.Variants.Where(v => v.IsActive).Max(v => (decimal?)v.SellingPrice) ?? 0m,
            p.CreatedAtUtc,
            p.UpdatedAtUtc));

        var rows = await PagedResult<ProductRow>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);

        var canViewCost = CatalogAccess.CanViewCost(_currentUser.Role);
        var items = rows.Items.Select(r => ToDto(r, canViewCost)).ToList();

        return new PagedResult<ProductDto>(items, rows.Page, rows.PageSize, rows.TotalCount, rows.TotalPages);
    }

    public async Task<ProductDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var product = await _db.Products.AsNoTracking()
            .Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.ProductNotFound, "Product not found.");

        return await BuildDetailAsync(product, tenantId, cancellationToken);
    }

    public async Task<ProductDetailDto> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _createValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        await RequireCategoryAsync(tenantId, request.CategoryId, cancellationToken);

        var hasExplicitVariants = request.Variants is { Count: > 0 };

        Product product;
        if (hasExplicitVariants)
        {
            product = Product.Create(tenantId, request.CategoryId, request.Name, request.Description, request.TrackInventory,
                new VariantSpec("Default", null, null, 0m, 0m));
            foreach (var variant in request.Variants!)
            {
                product.AddVariant(new VariantSpec(variant.Name, variant.Sku, variant.Barcode, variant.CostPrice, variant.SellingPrice));
            }
        }
        else
        {
            product = Product.Create(tenantId, request.CategoryId, request.Name, request.Description, request.TrackInventory,
                new VariantSpec("Default", request.Sku, request.Barcode, request.CostPrice, request.SellingPrice));
        }

        await GuardCodesAsync(tenantId, product.Variants.Select(v => (v.Sku, v.Barcode)), excludeProductId: null, cancellationToken);

        _db.Products.Add(product);
        await SaveOrTranslateAsync(cancellationToken);

        return await GetAsync(product.Id, cancellationToken);
    }

    public async Task<ProductDetailDto> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _updateValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var product = await _db.Products
            .Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.ProductNotFound, "Product not found.");

        await RequireCategoryAsync(tenantId, request.CategoryId, cancellationToken);

        product.UpdateDetails(request.CategoryId, request.Name, request.Description, request.TrackInventory);

        if (!product.HasVariants)
        {
            await GuardCodesAsync(tenantId, [(request.Sku, request.Barcode)], excludeProductId: id, cancellationToken);
            product.UpdateDefaultVariant(request.Sku, request.Barcode, request.CostPrice, request.SellingPrice);
        }

        if (request.IsActive)
        {
            product.Activate();
        }
        else
        {
            product.Deactivate();
        }

        await SaveOrTranslateAsync(cancellationToken);

        return await GetAsync(product.Id, cancellationToken);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var product = await _db.Products
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.ProductNotFound, "Product not found.");

        product.Deactivate();
        await _db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<ProductDetailDto> BuildDetailAsync(Product product, Guid tenantId, CancellationToken cancellationToken)
    {
        var categoryName = await _db.Categories.AsNoTracking()
            .Where(c => c.Id == product.CategoryId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var canViewCost = CatalogAccess.CanViewCost(_currentUser.Role);
        var active = product.Variants.Where(v => v.IsActive).ToList();
        var defaultVariant = product.Variants.FirstOrDefault(v => v.IsDefault);

        var dto = new ProductDto(
            product.Id,
            product.CategoryId,
            categoryName,
            product.Name,
            product.Description,
            product.TrackInventory,
            product.IsActive,
            product.HasVariants,
            product.Variants.Count(v => !v.IsDefault),
            product.HasVariants ? null : defaultVariant?.Sku,
            product.HasVariants ? null : defaultVariant?.Barcode,
            canViewCost && active.Count > 0 ? active.Min(v => v.CostPrice) : null,
            canViewCost && active.Count > 0 ? active.Max(v => v.CostPrice) : null,
            active.Count > 0 ? active.Min(v => v.SellingPrice) : 0m,
            active.Count > 0 ? active.Max(v => v.SellingPrice) : 0m,
            product.CreatedAtUtc,
            product.UpdatedAtUtc);

        var variants = product.Variants
            .Where(v => !product.HasVariants || !v.IsDefault)
            .OrderByDescending(v => v.IsDefault)
            .ThenBy(v => v.Name)
            .Select(v => new ProductVariantDto(
                v.Id, v.Name, v.IsDefault, v.Sku, v.Barcode,
                canViewCost ? v.CostPrice : null, v.SellingPrice, v.IsActive,
                v.CreatedAtUtc, v.UpdatedAtUtc))
            .ToList();

        return new ProductDetailDto(dto, variants);
    }

    private static ProductDto ToDto(ProductRow r, bool canViewCost) => new(
        r.Id, r.CategoryId, r.CategoryName, r.Name, r.Description, r.TrackInventory, r.IsActive,
        r.HasVariants, r.VariantCount, r.Sku, r.Barcode,
        canViewCost ? r.MinCost : null,
        canViewCost ? r.MaxCost : null,
        r.MinSelling, r.MaxSelling, r.CreatedAtUtc, r.UpdatedAtUtc);

    private static IQueryable<Product> ApplySort(IQueryable<Product> query, string? sortBy, string? sortDirection)
    {
        var descending = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        return (sortBy?.Trim().ToLowerInvariant()) switch
        {
            "sellingprice" => descending
                ? query.OrderByDescending(p => p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.SellingPrice))
                : query.OrderBy(p => p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.SellingPrice)),
            "createdatutc" => descending
                ? query.OrderByDescending(p => p.CreatedAtUtc)
                : query.OrderBy(p => p.CreatedAtUtc),
            _ => descending
                ? query.OrderByDescending(p => p.Name)
                : query.OrderBy(p => p.Name),
        };
    }

    private async Task RequireCategoryAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken)
    {
        var exists = await _db.Categories.AnyAsync(c => c.TenantId == tenantId && c.Id == categoryId, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");
        }
    }

    private async Task GuardCodesAsync(
        Guid tenantId,
        IEnumerable<(string? Sku, string? Barcode)> codes,
        Guid? excludeProductId,
        CancellationToken cancellationToken)
    {
        var list = codes.ToList();
        var skus = list.Select(c => Clean(c.Sku)).Where(s => s is not null).Cast<string>().Distinct().ToList();
        var barcodes = list.Select(c => Clean(c.Barcode)).Where(s => s is not null).Cast<string>().Distinct().ToList();

        if (skus.Count > 0)
        {
            var clash = await _db.ProductVariants.AnyAsync(
                v => v.TenantId == tenantId && v.Sku != null && skus.Contains(v.Sku)
                     && (excludeProductId == null || v.ProductId != excludeProductId),
                cancellationToken);
            if (clash)
            {
                throw new ConflictException(ErrorCodes.SkuAlreadyExists, "A product or variant with this SKU already exists.");
            }
        }

        if (barcodes.Count > 0)
        {
            var clash = await _db.ProductVariants.AnyAsync(
                v => v.TenantId == tenantId && v.Barcode != null && barcodes.Contains(v.Barcode)
                     && (excludeProductId == null || v.ProductId != excludeProductId),
                cancellationToken);
            if (clash)
            {
                throw new ConflictException(ErrorCodes.BarcodeAlreadyExists, "A product or variant with this barcode already exists.");
            }
        }
    }

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
