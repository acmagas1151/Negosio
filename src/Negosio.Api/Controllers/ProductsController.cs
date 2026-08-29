using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Catalog;
using Negosio.Application.Common;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/products")]
public sealed class ProductsController : ControllerBase
{
    private readonly IProductService _products;

    public ProductsController(IProductService products)
    {
        _products = products;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ProductDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ProductDto>>> List(
        [FromQuery] ProductListQuery query,
        CancellationToken cancellationToken)
        => Ok(await _products.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProductDetailDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductDetailDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _products.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.CatalogWrite)]
    [ProducesResponseType(typeof(ProductDetailDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ProductDetailDto>> Create(
        [FromBody] CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _products.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Product.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.CatalogWrite)]
    [ProducesResponseType(typeof(ProductDetailDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductDetailDto>> Update(
        Guid id,
        [FromBody] UpdateProductRequest request,
        CancellationToken cancellationToken)
        => Ok(await _products.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.CatalogWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await _products.DeactivateAsync(id, cancellationToken);
        return NoContent();
    }
}
