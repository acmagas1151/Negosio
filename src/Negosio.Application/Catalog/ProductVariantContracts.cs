namespace Negosio.Application.Catalog;

public interface IProductVariantService
{
    Task<IReadOnlyList<ProductVariantDto>> ListAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<ProductVariantDto> CreateAsync(Guid productId, VariantInput input, CancellationToken cancellationToken = default);

    Task<ProductVariantDto> UpdateAsync(Guid productId, Guid variantId, VariantInput input, CancellationToken cancellationToken = default);

    Task DeactivateAsync(Guid productId, Guid variantId, CancellationToken cancellationToken = default);
}
