using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Common;
using Negosio.Application.Reports;

namespace Negosio.Api.Controllers;

/// <summary>
/// Owner/Admin tenant-wide, Manager their own branch only (service-enforced — see
/// <c>ReportsService.BaseSalesAsync</c>/<c>BaseReturnsAsync</c>, same pattern as SalesController).
/// Cashier and every other role never reach these actions at all.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.ReportsView)]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportsService _reports;

    public ReportsController(IReportsService reports)
    {
        _reports = reports;
    }

    [HttpGet("overview")]
    [ProducesResponseType(typeof(ReportsOverviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReportsOverviewDto>> Overview(
        [FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => Ok(await _reports.GetOverviewAsync(filter, cancellationToken));

    [HttpGet("top-products")]
    [ProducesResponseType(typeof(IReadOnlyList<TopProductDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TopProductDto>>> TopProducts(
        [FromQuery] ReportFilter filter, [FromQuery] int top, CancellationToken cancellationToken)
        => Ok(await _reports.GetTopProductsAsync(filter, top <= 0 ? 10 : top, cancellationToken));

    [HttpGet("categories")]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryPerformanceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryPerformanceDto>>> Categories(
        [FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => Ok(await _reports.GetCategoryPerformanceAsync(filter, cancellationToken));

    [HttpGet("deliveries")]
    [ProducesResponseType(typeof(DeliveryReportResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryReportResultDto>> Deliveries(
        [FromQuery] DeliveryReportQuery query, CancellationToken cancellationToken)
        => Ok(await _reports.GetDeliveriesAsync(query, cancellationToken));

    [HttpGet("delivery-fulfillment")]
    [ProducesResponseType(typeof(DeliveryFulfillmentReportResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryFulfillmentReportResultDto>> DeliveryFulfillment(
        [FromQuery] DeliveryFulfillmentReportQuery query, CancellationToken cancellationToken)
        => Ok(await _reports.GetDeliveryFulfillmentAsync(query, cancellationToken));

    [HttpGet("pickup")]
    [ProducesResponseType(typeof(PickupReportResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PickupReportResultDto>> Pickups(
        [FromQuery] PickupReportQuery query, CancellationToken cancellationToken)
        => Ok(await _reports.GetPickupsAsync(query, cancellationToken));

    [HttpGet("branch-performance")]
    [ProducesResponseType(typeof(BranchPerformanceResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchPerformanceResultDto>> BranchPerformance(
        [FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => Ok(await _reports.GetBranchPerformanceAsync(filter, cancellationToken));

    [HttpGet("register-performance")]
    [ProducesResponseType(typeof(RegisterPerformanceResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterPerformanceResultDto>> RegisterPerformance(
        [FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => Ok(await _reports.GetRegisterPerformanceAsync(filter, cancellationToken));

    [HttpGet("register-sessions")]
    [ProducesResponseType(typeof(PagedResult<RegisterSessionReconciliationRowDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<RegisterSessionReconciliationRowDto>>> RegisterSessionReconciliation(
        [FromQuery] RegisterSessionReconciliationQuery query, CancellationToken cancellationToken)
        => Ok(await _reports.GetRegisterSessionReconciliationAsync(query, cancellationToken));

    [HttpGet("cashier-performance")]
    [ProducesResponseType(typeof(CashierPerformanceResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CashierPerformanceResultDto>> CashierPerformance(
        [FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => Ok(await _reports.GetCashierPerformanceAsync(filter, cancellationToken));
}
