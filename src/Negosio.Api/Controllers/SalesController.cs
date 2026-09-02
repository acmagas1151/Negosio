using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Common;
using Negosio.Application.Sales;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.SalesView)]
[Route("api/sales")]
public sealed class SalesController : ControllerBase
{
    private readonly ISaleQueryService _sales;
    private readonly IReceiptService _receipts;
    private readonly IReturnService _returns;
    private readonly IVoidSaleService _voidSale;

    public SalesController(ISaleQueryService sales, IReceiptService receipts, IReturnService returns, IVoidSaleService voidSale)
    {
        _sales = sales;
        _receipts = receipts;
        _returns = returns;
        _voidSale = voidSale;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<SaleSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<SaleSummaryDto>>> List(
        [FromQuery] SaleListQuery query,
        CancellationToken cancellationToken)
        => Ok(await _sales.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SaleDetailDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SaleDetailDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sales.GetAsync(id, cancellationToken));

    [HttpGet("{id:guid}/receipt")]
    [ProducesResponseType(typeof(ReceiptDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReceiptDto>> Receipt(Guid id, CancellationToken cancellationToken)
        => Ok(await _receipts.GetReceiptAsync(id, cancellationToken));

    [HttpGet("{id:guid}/returns")]
    [ProducesResponseType(typeof(IReadOnlyList<SaleReturnDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SaleReturnDto>>> Returns(Guid id, CancellationToken cancellationToken)
        => Ok(await _returns.ListReturnsAsync(id, cancellationToken));

    [HttpPost("{id:guid}/returns")]
    [Authorize(Policy = AuthorizationPolicies.RefundManage)]
    [ProducesResponseType(typeof(SaleReturnDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<SaleReturnDto>> CreateReturn(
        Guid id,
        [FromBody] CreateReturnRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _returns.CreateReturnAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(Returns), new { id }, created);
    }

    // No policy override — the controller's class-level SalesView already restricts this to
    // Owner/Admin/Manager/Cashier, which is exactly the void-participating role set;
    // InventoryStaff/Viewer/KitchenStaff never reach the action method at all.
    [HttpPost("{id:guid}/void")]
    [ProducesResponseType(typeof(SaleDetailDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SaleDetailDto>> Void(
        Guid id, [FromBody] VoidSaleRequest request, CancellationToken cancellationToken)
        => Ok(await _voidSale.VoidAsync(id, request, cancellationToken));
}
