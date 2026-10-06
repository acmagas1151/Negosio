using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Application.Pos;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Resto;

public interface IRestoSettlementService
{
    /// <summary>
    /// Settles an order into one Sale (spec 6.2). A retry with the same <see cref="SettleRestoOrderRequest.SettlementRequestId"/>
    /// returns the original Sale without creating or deducting anything again.
    /// </summary>
    Task<RestoSettlementResultDto> SettleAsync(Guid orderId, SettleRestoOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a released Bill-Out order unpaid (spec 5.3). Creates no Sale, Payment, sale number, or cash
    /// movement. Records waste for consumed items and releases table occupancy.
    /// </summary>
    Task<RestoOrderDto> UnpaidCloseAsync(Guid orderId, UnpaidCloseRestoOrderRequest request, CancellationToken cancellationToken = default);
}

public sealed class RestoSettlementService : IRestoSettlementService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IDocumentNumberService _documentNumbers;
    private readonly IInventoryPosting _inventory;
    private readonly IRestoUnpaidCloseAuthorizationResolver _unpaidAuth;
    private readonly IRestoOrderService _orders;
    private readonly IValidator<SettleRestoOrderRequest> _settleValidator;
    private readonly IValidator<UnpaidCloseRestoOrderRequest> _unpaidValidator;
    private readonly TimeProvider _timeProvider;

    public RestoSettlementService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IBranchAccessResolver branchAccess,
        IDocumentNumberService documentNumbers,
        IInventoryPosting inventory,
        IRestoUnpaidCloseAuthorizationResolver unpaidAuth,
        IRestoOrderService orders,
        IValidator<SettleRestoOrderRequest> settleValidator,
        IValidator<UnpaidCloseRestoOrderRequest> unpaidValidator,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
        _documentNumbers = documentNumbers;
        _inventory = inventory;
        _unpaidAuth = unpaidAuth;
        _orders = orders;
        _settleValidator = settleValidator;
        _unpaidValidator = unpaidValidator;
        _timeProvider = timeProvider;
    }

    // ---- Settlement ----------------------------------------------------------------------------

    public async Task<RestoSettlementResultDto> SettleAsync(Guid orderId, SettleRestoOrderRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _settleValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        // 1. Idempotency: decided before any lock, so a retry after a lost response is cheap and deducts nothing.
        var prior = await _db.RestoOrders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RestoOrderNotFound, "Order not found.");
        await GuardBranchAsync(prior.BranchId, cancellationToken);

        if (prior.Status == RestoOrderStatus.Settled)
        {
            if (prior.SettlementRequestId == request.SettlementRequestId && prior.SaleId is { } priorSaleId)
            {
                return await ResultForExistingSaleAsync(tenantId, priorSaleId, cancellationToken);
            }

            throw new ConflictException(ErrorCodes.RestoOrderAlreadySettled,
                "This order has already been settled.");
        }

        if (prior.Status != RestoOrderStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RestoOrderNotOpen, "This order is no longer open.");
        }

        // 2. The caller's own open session on this branch (spec R7).
        var session = await _db.RegisterSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == request.RegisterSessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");
        if (session.BranchId != prior.BranchId || session.OpenedByUserId != _currentUser.UserId || session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RestoSessionRequired,
                "Settle the order from your own open register session on this branch.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // 3. Session lock first, then the order lock (spec 6.1). Only settlement takes both.
        await _db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {session.Id}")
            .ToListAsync(cancellationToken);

        var sessionStatus = await _db.RegisterSessions.AsNoTracking()
            .Where(s => s.Id == session.Id).Select(s => s.Status).SingleAsync(cancellationToken);
        if (sessionStatus != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RegisterSessionNotOpen, "The register session is not open.");
        }

        var order = await RestoOrderLocking.LockAndLoadAsync(_db, _branchAccess, tenantId, orderId, request.ExpectedRowVersion, cancellationToken);
        if (order.Status != RestoOrderStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RestoOrderNotOpen, "This order is no longer open.");
        }

        // 4. Rules that depend on the locked state.
        if (order.ServiceType == RestoServiceType.BillOut
            && order.Rounds.Any(r => r.Status == RestoOrderRoundStatus.Draft && r.Items.Any(i => i.VoidedAtUtc is null)))
        {
            throw new BusinessRuleException(ErrorCodes.RestoDraftRoundsPending,
                "Release or void the unsent round before settling this bill.");
        }

        var billable = order.Rounds
            .Where(r => r.Status != RestoOrderRoundStatus.Voided)
            .OrderBy(r => r.RoundNumber)
            .SelectMany(r => r.Items.OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id))
            .Where(i => i.VoidedAtUtc is null)
            .ToList();
        if (billable.Count == 0)
        {
            throw new BusinessRuleException(ErrorCodes.RestoNoBillableItems, "There is nothing to bill on this order.");
        }

        // 5. Totals and payments from the frozen item amounts only (spec 4.2).
        var totals = SaleTotals.Compute(
            billable.Select(i => new SaleLineCalculator.Line(i.GrossAmount, i.DiscountAmount, i.TaxAmount, i.NetAmount)),
            order.PricesIncludeTaxSnapshot, deliveryCharge: 0m);
        var payments = PaymentResolver.Resolve(request.Payments, totals.GrandTotal);
        var amountPaid = payments.Sum(p => p.Amount);
        var changeDue = Money.Round(payments.Sum(p => p.ChangeAmount ?? 0m));

        var branch = await _db.Branches.AsNoTracking().SingleAsync(b => b.TenantId == tenantId && b.Id == order.BranchId, cancellationToken);
        var variantIds = billable.Select(i => i.ProductVariantId).Distinct().ToList();
        var tracked = await (
            from v in _db.ProductVariants.AsNoTracking()
            join p in _db.Products.AsNoTracking() on v.ProductId equals p.Id
            where v.TenantId == tenantId && variantIds.Contains(v.Id)
            select new { v.Id, p.TrackInventory }
        ).ToDictionaryAsync(x => x.Id, x => x.TrackInventory, cancellationToken);

        // 6. The Sale, through checkout's own numbering and idempotency pattern.
        var saleNumber = await _documentNumbers.NextAsync(tenantId, order.BranchId, DocumentNumberType.Sale, branch.Code, cancellationToken);
        var sale = Sale.Begin(tenantId, order.BranchId, session.Id, saleNumber, request.SettlementRequestId, _currentUser.UserId, SaleOrigin.Resto);

        foreach (var item in billable)
        {
            var saleItem = sale.AddItem(
                item.ProductVariantId, item.ProductNameSnapshot, item.VariantNameSnapshot, null, null,
                item.UnitPriceSnapshot, item.Quantity, item.DiscountKind, item.DiscountValue,
                item.GrossAmount, item.DiscountAmount, item.TaxAmount, item.NetAmount, item.CostPriceSnapshot);

            foreach (var modifier in item.Modifiers.OrderBy(m => m.CreatedAtUtc))
            {
                saleItem.AddModifier(modifier.ModifierGroupNameSnapshot, modifier.ModifierOptionNameSnapshot, modifier.PriceDeltaSnapshot);
            }
        }

        foreach (var p in payments)
        {
            sale.AddPayment(p.Method, p.Amount, p.ReferenceNumber, p.ReceivedAmount, p.ChangeAmount);
        }

        sale.Complete(totals.Subtotal, totals.DiscountTotal, totals.TaxTotal, 0m, totals.GrandTotal, amountPaid, changeDue);
        _db.Sales.Add(sale);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.TryGetConstraintName(ex, out var name)
                                           && name?.Contains("ClientRequestId", StringComparison.OrdinalIgnoreCase) == true)
        {
            // A concurrent duplicate of the same settlement won the race; return its result.
            await transaction.RollbackAsync(cancellationToken);
            return await ResultForExistingSaleAsync(tenantId, request.SettlementRequestId, cancellationToken, byClientRequest: true);
        }

        // 7. Stock: exactly once, at settlement, for each tracked line (spec 5.2).
        foreach (var item in billable.Where(i => tracked.GetValueOrDefault(i.ProductVariantId)))
        {
            await _inventory.DeductForSaleAsync(
                tenantId, order.BranchId, item.ProductVariantId, item.Quantity, sale.Id, _currentUser.UserId, cancellationToken);
        }

        // 8. Settle the order and commit everything together.
        order.Settle(sale.Id, request.SettlementRequestId, _timeProvider.GetUtcNow().UtcDateTime);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RestoSettlementResultDto(
            sale.Id, sale.SaleNumber, totals.Subtotal, totals.DiscountTotal, totals.TaxTotal, totals.GrandTotal,
            amountPaid, changeDue, WasExisting: false);
    }

    // ---- Unpaid closure ------------------------------------------------------------------------

    public async Task<RestoOrderDto> UnpaidCloseAsync(Guid orderId, UnpaidCloseRestoOrderRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _unpaidValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var prior = await _db.RestoOrders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RestoOrderNotFound, "Order not found.");
        await GuardBranchAsync(prior.BranchId, cancellationToken);

        if (prior.Status == RestoOrderStatus.UnpaidClosed && prior.UnpaidClosureRequestId == request.ClosureRequestId)
        {
            return await _orders.GetAsync(orderId, cancellationToken);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var order = await RestoOrderLocking.LockAndLoadAsync(_db, _branchAccess, tenantId, orderId, request.ExpectedRowVersion, cancellationToken);

        if (order.Status != RestoOrderStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RestoOrderNotOpen, "This order is no longer open.");
        }

        if (order.ServiceType != RestoServiceType.BillOut)
        {
            throw new BusinessRuleException(ErrorCodes.RestoUnpaidCloseBillOutOnly, "Only a Bill-Out order can be closed unpaid.");
        }

        var released = order.Rounds.Where(r => r.Status == RestoOrderRoundStatus.Released).ToList();
        if (released.Count == 0)
        {
            throw new BusinessRuleException(ErrorCodes.RestoUnpaidCloseRequiresReleasedRound,
                "Nothing has been sent to the kitchen. Cancel the order instead.");
        }

        var approverId = await _unpaidAuth.ResolveAsync(order.BranchId, request.Approval, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var consumed = released.SelectMany(r => r.Items)
            .Where(i => i.VoidedAtUtc is null && RestoWastePolicy.IsConsumed(i.KitchenStatus))
            .ToList();
        var variantIds = consumed.Select(i => i.ProductVariantId).Distinct().ToList();
        var tracked = await (
            from v in _db.ProductVariants.AsNoTracking()
            join p in _db.Products.AsNoTracking() on v.ProductId equals p.Id
            where v.TenantId == tenantId && variantIds.Contains(v.Id)
            select new { v.Id, p.TrackInventory }
        ).ToDictionaryAsync(x => x.Id, x => x.TrackInventory, cancellationToken);

        foreach (var item in consumed.Where(i => tracked.GetValueOrDefault(i.ProductVariantId)))
        {
            await _inventory.DeductForWasteAsync(
                tenantId, order.BranchId, item.ProductVariantId, item.Quantity,
                "RestoOrder", order.Id, $"Unpaid closure: {request.Reason.Trim()}", _currentUser.UserId, cancellationToken);
        }

        order.UnpaidClose(_currentUser.UserId, request.Reason, approverId, request.ClosureRequestId, now);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await _orders.GetAsync(orderId, cancellationToken);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private async Task<RestoSettlementResultDto> ResultForExistingSaleAsync(
        Guid tenantId, Guid key, CancellationToken cancellationToken, bool byClientRequest = false)
    {
        var sale = byClientRequest
            ? await _db.Sales.AsNoTracking().SingleAsync(s => s.TenantId == tenantId && s.ClientRequestId == key, cancellationToken)
            : await _db.Sales.AsNoTracking().SingleAsync(s => s.TenantId == tenantId && s.Id == key, cancellationToken);
        return ToResult(sale);
    }

    private static RestoSettlementResultDto ToResult(Sale sale) => new(
        sale.Id, sale.SaleNumber, sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.GrandTotal,
        sale.AmountPaid, sale.ChangeDue, WasExisting: true);

    private async Task GuardBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var assigned = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } scoped && scoped != branchId)
        {
            throw new NotFoundException(ErrorCodes.RestoOrderNotFound, "Order not found.");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ErrorCodes.RestoOrderConcurrencyConflict,
                "This order was changed by someone else. Refresh and try again.");
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
}
