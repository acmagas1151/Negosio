using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Catalog;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/products/{productId:guid}/variants")]
public sealed class ProductVariantsController : ControllerBase
{
    private readonly IProductVariantService _variants;

    public ProductVariantsController(IProductVariantService variants)
    {
        _variants = variants;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProductVariantDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductVariantDto>>> List(Guid productId, CancellationToken cancellationToken)
        => Ok(await _variants.ListAsync(productId, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.CatalogWrite)]
    [ProducesResponseType(typeof(ProductVariantDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ProductVariantDto>> Create(
        Guid productId,
        [FromBody] VariantInput input,
        CancellationToken cancellationToken)
    {
        var created = await _variants.CreateAsync(productId, input, cancellationToken);
        return CreatedAtAction(nameof(List), new { productId }, created);
    }

    [HttpPut("{variantId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.CatalogWrite)]
    [ProducesResponseType(typeof(ProductVariantDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductVariantDto>> Update(
        Guid productId,
        Guid variantId,
        [FromBody] VariantInput input,
        CancellationToken cancellationToken)
        => Ok(await _variants.UpdateAsync(productId, variantId, input, cancellationToken));

    [HttpDelete("{variantId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.CatalogWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        await _variants.DeactivateAsync(productId, variantId, cancellationToken);
        return NoContent();
    }
}
