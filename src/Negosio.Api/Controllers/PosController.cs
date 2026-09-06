using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Common;
using Negosio.Application.Pos;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PosOperate)]
[Route("api/pos")]
public sealed class PosController : ControllerBase
{
    private readonly IPosCatalogService _catalog;
    private readonly ICheckoutService _checkout;
    private readonly IPosContextService _context;
    private readonly ITransactionCancellationService _transactionCancellation;

    public PosController(
        IPosCatalogService catalog, ICheckoutService checkout, IPosContextService context,
        ITransactionCancellationService transactionCancellation)
    {
        _catalog = catalog;
        _checkout = checkout;
        _context = context;
        _transactionCancellation = transactionCancellation;
    }

    /// <summary>The branch this POS session runs against (auto for branch-scoped roles).</summary>
    [HttpGet("context")]
    [ProducesResponseType(typeof(PosContextDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PosContextDto>> Context(CancellationToken cancellationToken)
        => Ok(await _context.GetContextAsync(cancellationToken));

    /// <summary>Active registers in the resolved branch, each with its open-session state.</summary>
    [HttpGet("registers")]
    [ProducesResponseType(typeof(IReadOnlyList<PosRegisterDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PosRegisterDto>>> Registers(
        [FromQuery] Guid? branchId, CancellationToken cancellationToken)
        => Ok(await _context.GetRegistersAsync(branchId, cancellationToken));

    [HttpGet("catalog")]
    [ProducesResponseType(typeof(PagedResult<PosCatalogItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<PosCatalogItemDto>>> Catalog(
        [FromQuery] PosCatalogQuery query,
        CancellationToken cancellationToken)
        => Ok(await _catalog.SearchAsync(query, cancellationToken));

    [HttpGet("catalog/barcode/{barcode}")]
    [ProducesResponseType(typeof(PosCatalogItemDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PosCatalogItemDto>> Barcode(
        string barcode,
        [FromQuery] Guid branchId,
        CancellationToken cancellationToken)
        => Ok(await _catalog.BarcodeLookupAsync(branchId, barcode, cancellationToken));

    [HttpPost("checkout")]
    [ProducesResponseType(typeof(SaleResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SaleResultDto>> Checkout(
        [FromBody] CheckoutRequest request,
        CancellationToken cancellationToken)
        => Ok(await _checkout.CheckoutAsync(request, cancellationToken));

    /// <summary>
    /// Authorizes cancelling the cart currently at the register — no Sale exists yet for it, so
    /// this never creates one or consumes a SaleNumber; it only checks whether the cashier may
    /// discard the cart directly or needs Manager/Admin/Owner approval first.
    /// </summary>
    [HttpPost("transactions/cancel/authorize")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> AuthorizeCancelTransaction(
        [FromBody] CancelTransactionAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        await _transactionCancellation.AuthorizeCancelAsync(request, cancellationToken);
        return NoContent();
    }
}
