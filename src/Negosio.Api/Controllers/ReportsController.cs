using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
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
}
