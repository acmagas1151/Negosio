using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Resto;

public interface IRestoOrderService
{
    Task<RestoOrderDto> OpenAsync(OpenRestoOrderRequest request, CancellationToken cancellationToken = default);

    Task<RestoOrderDto> GetAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<RestoOrderDto> AddRoundAsync(Guid orderId, RestoStructuralRequest request, CancellationToken cancellationToken = default);

    Task<RestoOrderDto> ReleaseRoundAsync(Guid orderId, Guid roundId, RestoStructuralRequest request, CancellationToken cancellationToken = default);

    Task<RestoOrderDto> AddItemAsync(Guid orderId, AddRestoItemRequest request, CancellationToken cancellationToken = default);

    Task<RestoOrderDto> VoidItemAsync(Guid orderId, Guid itemId, VoidRestoItemRequest request, CancellationToken cancellationToken = default);

    Task<RestoOrderDto> CancelAsync(Guid orderId, CancelRestoOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovery release for a settled Pay-as-you-order whose round was not released by the first attempt
    /// (design spec 6.4). Safe to call repeatedly and from several app instances: a round already released,
    /// voided, or not yet eligible is a no-op. No user actor and no client RowVersion.
    /// </summary>
    Task ReleaseRoundBySystemAsync(Guid orderId, Guid roundId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingPayoReleaseDto>> ListPendingPayoReleasesAsync(DateTime settledBeforeUtc, Guid? branchId, CancellationToken cancellationToken = default);
}

public sealed class RestoOrderService : IRestoOrderService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IUserPermissionGrantService _grants;
    private readonly IApproverVerificationService _approverVerification;
    private readonly IRestoItemVoidAuthorizationResolver _itemVoidAuth;
    private readonly IRestoOrderCancelAuthorizationResolver _cancelAuth;
    private readonly IInventoryPosting _inventory;
    private readonly IValidator<OpenRestoOrderRequest> _openValidator;
    private readonly IValidator<RestoStructuralRequest> _structuralValidator;
    private readonly IValidator<AddRestoItemRequest> _addItemValidator;
    private readonly IValidator<VoidRestoItemRequest> _voidValidator;
    private readonly IValidator<CancelRestoOrderRequest> _cancelValidator;
    private readonly TimeProvider _timeProvider;

    public RestoOrderService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IBranchAccessResolver branchAccess,
        IUserPermissionGrantService grants,
        IApproverVerificationService approverVerification,
        IRestoItemVoidAuthorizationResolver itemVoidAuth,
        IRestoOrderCancelAuthorizationResolver cancelAuth,
        IInventoryPosting inventory,
        IValidator<OpenRestoOrderRequest> openValidator,
        IValidator<RestoStructuralRequest> structuralValidator,
        IValidator<AddRestoItemRequest> addItemValidator,
        IValidator<VoidRestoItemRequest> voidValidator,
        IValidator<CancelRestoOrderRequest> cancelValidator,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
        _grants = grants;
        _approverVerification = approverVerification;
        _itemVoidAuth = itemVoidAuth;
        _cancelAuth = cancelAuth;
        _inventory = inventory;
        _openValidator = openValidator;
        _structuralValidator = structuralValidator;
        _addItemValidator = addItemValidator;
        _voidValidator = voidValidator;
        _cancelValidator = cancelValidator;
        _timeProvider = timeProvider;
    }

    // ---- Open ----------------------------------------------------------------------------------

    public async Task<RestoOrderDto> OpenAsync(OpenRestoOrderRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _openValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var branchId = await _branchAccess.ResolveTargetBranchAsync(request.BranchId, cancellationToken: cancellationToken);
        var branch = await _db.Branches.AsNoTracking()
            .SingleAsync(b => b.TenantId == tenantId && b.Id == branchId, cancellationToken);
        if (!branch.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.BranchInactive, "This branch is inactive.");
        }

        var enabled = request.ServiceType switch
        {
            RestoServiceType.PayAsYouOrder => branch.SupportsPayAsYouOrder,
            RestoServiceType.BillOut => branch.SupportsBillOut,
            _ => false,
        };
        if (!enabled)
        {
            throw new BusinessRuleException(ErrorCodes.RestoServiceTypeNotEnabled, "This service type is not enabled for this branch.");
        }

        var session = await _db.RegisterSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == request.RegisterSessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");
        if (session.BranchId != branchId || session.OpenedByUserId != _currentUser.UserId || session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RestoSessionRequired,
                "Open the order from your own open register session on this branch.");
        }

        var tenant = await _db.TenantProfiles.AsNoTracking().SingleAsync(p => p.Id == tenantId, cancellationToken);

        RestoOrder order;
        if (request.ServiceType == RestoServiceType.BillOut)
        {
            if (request.TableId is not { } tableId)
            {
                throw new BusinessRuleException(ErrorCodes.RestoTableRequired, "A Bill-Out order needs a table.");
            }

            var tableAvailable = await _db.RestoTables.AnyAsync(
                t => t.TenantId == tenantId && t.Id == tableId && t.BranchId == branchId && t.IsActive, cancellationToken);
            if (!tableAvailable)
            {
                throw new BusinessRuleException(ErrorCodes.RestoTableNotFound, "That table is not available at this branch.");
            }

            order = RestoOrder.OpenBillOut(
                tenantId, branchId, session.Id, _currentUser.UserId, tableId, request.DisplayLabel,
                tenant.PricesIncludeTax, tenant.TaxRatePercent);
        }
        else
        {
            order = RestoOrder.OpenPayAsYouOrder(
                tenantId, branchId, session.Id, _currentUser.UserId, request.DisplayLabel,
                tenant.PricesIncludeTax, tenant.TaxRatePercent);
        }

        _db.RestoOrders.Add(order);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.TryGetConstraintName(ex, out var name)
                                           && name?.Contains("TableId_Open", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ConflictException(ErrorCodes.RestoTableOccupied, "That table already has an open order.");
        }

        return await ProjectAsync(order.Id, cancellationToken);
    }

    public Task<RestoOrderDto> GetAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        ReadAsync(orderId, cancellationToken);

    // ---- Rounds --------------------------------------------------------------------------------

    public async Task<RestoOrderDto> AddRoundAsync(Guid orderId, RestoStructuralRequest request, CancellationToken cancellationToken = default)
    {
        await _structuralValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAndLoadOrderAsync(orderId, request.ExpectedRowVersion, cancellationToken);

        RequireOpen(order);
        if (order.ServiceType == RestoServiceType.PayAsYouOrder && order.Rounds.Count > 0)
        {
            throw new BusinessRuleException(ErrorCodes.RestoPayoSingleRound, "A Pay-as-you-order sale has exactly one round.");
        }

        order.OpenNextRound();
        order.RecordStructuralChange();
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ProjectAsync(orderId, cancellationToken);
    }

    public async Task<RestoOrderDto> ReleaseRoundAsync(Guid orderId, Guid roundId, RestoStructuralRequest request, CancellationToken cancellationToken = default)
    {
        await _structuralValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAndLoadOrderAsync(orderId, request.ExpectedRowVersion, cancellationToken);
        var round = FindRound(order, roundId);

        if (round.Status == RestoOrderRoundStatus.Voided)
        {
            throw new BusinessRuleException(ErrorCodes.RestoRoundNotDraft, "A voided round cannot be released.");
        }

        if (round.Status == RestoOrderRoundStatus.Released)
        {
            await transaction.CommitAsync(cancellationToken);
            return await ProjectAsync(orderId, cancellationToken);
        }

        RequireReleasable(order);
        round.Release(_currentUser.UserId, _timeProvider.GetUtcNow().UtcDateTime);
        order.RecordStructuralChange();
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ProjectAsync(orderId, cancellationToken);
    }

    public async Task ReleaseRoundBySystemAsync(Guid orderId, Guid roundId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAndLoadOrderAsync(orderId, expectedRowVersion: null, cancellationToken);
        var round = order.Rounds.SingleOrDefault(r => r.Id == roundId);

        if (round is { Status: RestoOrderRoundStatus.Draft } && IsUserReleasable(order))
        {
            round.ReleaseBySystem(_timeProvider.GetUtcNow().UtcDateTime);
            order.RecordStructuralChange();
            await SaveAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingPayoReleaseDto>> ListPendingPayoReleasesAsync(
        DateTime settledBeforeUtc, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var scope = await _branchAccess.AssignedBranchIdAsync(cancellationToken) ?? branchId;

        return await (
            from o in _db.RestoOrders.AsNoTracking()
            join r in _db.RestoOrderRounds.AsNoTracking() on o.Id equals r.RestoOrderId
            where o.TenantId == tenantId
                && o.ServiceType == RestoServiceType.PayAsYouOrder
                && o.Status == RestoOrderStatus.Settled
                && o.SettledAtUtc <= settledBeforeUtc
                && r.Status == RestoOrderRoundStatus.Draft
                && (scope == null || o.BranchId == scope)
                && _db.RestoOrderItems.Any(i => i.RestoOrderRoundId == r.Id && i.VoidedAtUtc == null)
            orderby o.SettledAtUtc
            select new PendingPayoReleaseDto(o.Id, r.Id, o.BranchId, o.SettledAtUtc!.Value)
        ).ToListAsync(cancellationToken);
    }

    // ---- Items ---------------------------------------------------------------------------------

    public async Task<RestoOrderDto> AddItemAsync(Guid orderId, AddRestoItemRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _addItemValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAndLoadOrderAsync(orderId, request.ExpectedRowVersion, cancellationToken);

        RequireOpen(order);
        var round = FindRound(order, request.RoundId);
        if (round.Status != RestoOrderRoundStatus.Draft)
        {
            throw new BusinessRuleException(ErrorCodes.RestoRoundNotDraft, "Items can only be added to a round that hasn't been released yet.");
        }

        var variant = await _db.ProductVariants.AsNoTracking()
            .SingleOrDefaultAsync(v => v.TenantId == tenantId && v.Id == request.ProductVariantId, cancellationToken);
        if (variant is null || !variant.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.RestoProductNotOrderable, "That item is not available to order.");
        }

        var product = await _db.Products.AsNoTracking()
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == variant.ProductId, cancellationToken);
        if (product is null || !product.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.RestoProductNotOrderable, "That item is not available to order.");
        }

        var station = await _db.RestoStations.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == request.StationId && s.BranchId == order.BranchId, cancellationToken)
            ?? throw new BusinessRuleException(ErrorCodes.RestoStationNotFound, "That station does not exist at this branch.");
        if (!station.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.RestoStationInactive, "That station is not active.");
        }

        var (options, groups) = await LoadModifiersAsync(tenantId, request.ModifierOptionIds, product.Id, cancellationToken);

        var discount = request.Discount ?? new CheckoutDiscountInput();
        var discountApprover = await EnsureDiscountAuthorizedAsync(order.BranchId, discount, request.Approval, cancellationToken);

        var effectiveUnitPrice = variant.SellingPrice + options.Sum(o => o.PriceDelta);
        var line = SaleLineCalculator.Calculate(
            effectiveUnitPrice, request.Quantity, discount.Type, discount.Value,
            order.TaxRatePercentSnapshot, order.PricesIncludeTaxSnapshot);

        var item = round.AddItem(
            variant.Id, product.Name, variant.IsDefault ? null : variant.Name, station.Id, station.Name,
            effectiveUnitPrice, order.TaxRatePercentSnapshot, line.Gross, line.Discount, line.Tax, line.Net,
            request.Quantity, request.KitchenNote, discount.Type, discount.Value, discountApprover, variant.CostPrice);

        foreach (var option in options)
        {
            item.AddModifier(groups[option.ModifierGroupId].Name, option.Name, option.PriceDelta);
        }

        order.RecordStructuralChange();
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ProjectAsync(orderId, cancellationToken);
    }

    /// <summary>
    /// Voids one item. Authorization and the waste decision are both based on the kitchen status observed
    /// under the order lock. The item is then claimed with a conditional update on that same status, so a
    /// KDS transition racing this void either lands first (and this void re-decides) or loses cleanly. One
    /// retry is allowed; after that the caller refreshes.
    /// </summary>
    public async Task<RestoOrderDto> VoidItemAsync(Guid orderId, Guid itemId, VoidRestoItemRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _voidValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var order = await LockAndLoadOrderAsync(orderId, request.ExpectedRowVersion, cancellationToken);
            RequireOpen(order);

            var item = order.Rounds.SelectMany(r => r.Items).SingleOrDefault(i => i.Id == itemId)
                ?? throw new NotFoundException(ErrorCodes.RestoItemNotFound, "Item not found.");
            if (item.VoidedAtUtc is not null)
            {
                throw new BusinessRuleException(ErrorCodes.RestoItemAlreadyVoided, "This item has already been voided.");
            }

            var observed = item.KitchenStatus;
            var approverId = observed is null
                ? (Guid?)null
                : await _itemVoidAuth.ResolveAsync(order.BranchId, request.Approval, cancellationToken);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var reason = request.Reason.Trim();
            int? observedValue = observed is null ? null : (int)observed.Value;

            var claimed = await _db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE RestoOrderItems
                SET VoidedAtUtc = {now}, VoidedByUserId = {_currentUser.UserId}, ApprovedByUserId = {approverId},
                    VoidReason = {reason}, UpdatedAtUtc = {now}
                WHERE Id = {itemId} AND TenantId = {tenantId} AND VoidedAtUtc IS NULL
                  AND (KitchenStatus = {observedValue} OR (KitchenStatus IS NULL AND {observedValue} IS NULL))
                """, cancellationToken);

            if (claimed == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (attempt >= 2)
                {
                    throw new ConflictException(ErrorCodes.RestoOrderConcurrencyConflict,
                        "This item changed in the kitchen while it was being voided. Refresh and try again.");
                }

                continue;
            }

            if (RestoWastePolicy.IsConsumed(observed) && await TracksInventoryAsync(tenantId, item.ProductVariantId, cancellationToken))
            {
                await _inventory.DeductForWasteAsync(
                    tenantId, order.BranchId, item.ProductVariantId, item.Quantity,
                    "RestoOrderItem", item.Id, reason, _currentUser.UserId, cancellationToken);
            }

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE RestoOrders SET UpdatedAtUtc = {now} WHERE Id = {orderId} AND TenantId = {tenantId}", cancellationToken);
            await SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return await ProjectAsync(orderId, cancellationToken);
        }
    }

    // ---- Cancel --------------------------------------------------------------------------------

    public async Task<RestoOrderDto> CancelAsync(Guid orderId, CancelRestoOrderRequest request, CancellationToken cancellationToken = default)
    {
        await _cancelValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAndLoadOrderAsync(orderId, request.ExpectedRowVersion, cancellationToken);
        RequireOpen(order);

        if (order.Rounds.Any(r => r.Status == RestoOrderRoundStatus.Released))
        {
            throw new BusinessRuleException(ErrorCodes.RestoOrderCancelHasReleasedRound,
                "A round has already been sent to the kitchen. Close the order unpaid or settle it instead.");
        }

        var approverId = await _cancelAuth.ResolveAsync(order.BranchId, request.Approval, cancellationToken);
        order.Cancel(_currentUser.UserId, request.Reason, _timeProvider.GetUtcNow().UtcDateTime, approverId);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ProjectAsync(orderId, cancellationToken);
    }

    // ---- Shared helpers ------------------------------------------------------------------------

    /// <summary>
    /// Takes the order's pessimistic lock as the first statement of the caller's transaction, then loads the
    /// aggregate and enforces branch isolation. The expected RowVersion (when supplied) is compared after the
    /// lock, so the check cannot race a concurrent writer.
    /// </summary>
    private Task<RestoOrder> LockAndLoadOrderAsync(Guid orderId, byte[]? expectedRowVersion, CancellationToken cancellationToken) =>
        RestoOrderLocking.LockAndLoadAsync(_db, _branchAccess, RequireTenant(), orderId, expectedRowVersion, cancellationToken);

    private async Task<RestoOrderDto> ReadAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var order = await _db.RestoOrders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RestoOrderNotFound, "Order not found.");
        await GuardBranchAsync(order.BranchId, cancellationToken);
        return await ProjectAsync(orderId, cancellationToken);
    }

    private async Task<RestoOrderDto> ProjectAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var order = await _db.RestoOrders.AsNoTracking()
            .Include(o => o.Rounds).ThenInclude(r => r.Items).ThenInclude(i => i.Modifiers)
            .SingleAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken);

        var rounds = order.Rounds
            .OrderBy(r => r.RoundNumber)
            .Select(r => new RestoRoundDto(
                r.Id, r.RoundNumber, r.Status, r.ReleasedAtUtc,
                r.Items.OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id).Select(ToItemDto).ToList()))
            .ToList();

        var live = order.Rounds.SelectMany(r => r.Items).Where(i => i.VoidedAtUtc is null)
            .Select(i => new SaleLineCalculator.Line(i.GrossAmount, i.DiscountAmount, i.TaxAmount, i.NetAmount));
        var totals = SaleTotals.Compute(live, order.PricesIncludeTaxSnapshot, deliveryCharge: 0m);
        var summary = new RestoOrderSummaryDto(totals.Subtotal, totals.DiscountTotal, totals.TaxTotal, totals.SaleTotal, totals.GrandTotal);

        return new RestoOrderDto(
            order.Id, order.BranchId, order.ServiceType, order.Status, order.TableId, order.DisplayLabel,
            order.RegisterSessionId, order.PricesIncludeTaxSnapshot, order.RowVersion, rounds, summary, order.SaleId);
    }

    private static RestoItemDto ToItemDto(RestoOrderItem i) => new(
        i.Id, i.ProductVariantId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.StationNameSnapshot,
        i.Quantity, i.UnitPriceSnapshot, i.GrossAmount, i.DiscountAmount, i.TaxAmount, i.NetAmount,
        i.KitchenStatus, i.VoidedAtUtc,
        i.Modifiers.OrderBy(m => m.CreatedAtUtc)
            .Select(m => new RestoModifierDto(m.ModifierGroupNameSnapshot, m.ModifierOptionNameSnapshot, m.PriceDeltaSnapshot))
            .ToList());

    private async Task<(List<ModifierOption> Options, Dictionary<Guid, ModifierGroup> Groups)> LoadModifiersAsync(
        Guid tenantId, IReadOnlyList<Guid> optionIds, Guid productId, CancellationToken cancellationToken)
    {
        if (optionIds.Count == 0)
        {
            var attachedGroups = await _db.ProductModifierGroups.AsNoTracking()
                .Where(p => p.TenantId == tenantId && p.ProductId == productId && p.IsRequired)
                .CountAsync(cancellationToken);
            if (attachedGroups > 0)
            {
                throw new BusinessRuleException(ErrorCodes.RestoRequiredModifierMissing, "A required modifier was not selected.");
            }

            return (new List<ModifierOption>(), new Dictionary<Guid, ModifierGroup>());
        }

        var found = await _db.ModifierOptions.AsNoTracking()
            .Where(o => o.TenantId == tenantId && optionIds.Contains(o.Id))
            .ToListAsync(cancellationToken);
        if (found.Count != optionIds.Count || found.Any(o => !o.IsActive))
        {
            throw new BusinessRuleException(ErrorCodes.RestoModifierOptionNotFound, "A selected modifier is not available.");
        }

        var options = optionIds.Select(id => found.Single(o => o.Id == id)).ToList();
        var groupIds = options.Select(o => o.ModifierGroupId).Distinct().ToList();
        var groups = await _db.ModifierGroups.AsNoTracking()
            .Where(g => g.TenantId == tenantId && groupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, cancellationToken);
        if (groups.Count != groupIds.Count || groups.Values.Any(g => !g.IsActive))
        {
            throw new BusinessRuleException(ErrorCodes.RestoModifierOptionNotFound, "A selected modifier is not available.");
        }

        var attached = await _db.ProductModifierGroups.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ProductId == productId)
            .ToListAsync(cancellationToken);
        if (options.Any(o => attached.All(a => a.ModifierGroupId != o.ModifierGroupId)))
        {
            throw new BusinessRuleException(ErrorCodes.RestoModifierSelectionInvalid, "A selected modifier does not apply to this item.");
        }

        foreach (var groupId in groupIds)
        {
            var picked = options.Count(o => o.ModifierGroupId == groupId);
            if (groups[groupId].SelectionType == ModifierSelectionType.Single && picked > 1)
            {
                throw new BusinessRuleException(ErrorCodes.RestoModifierSelectionInvalid, "Only one option can be chosen from this modifier group.");
            }
        }

        foreach (var required in attached.Where(a => a.IsRequired))
        {
            if (!options.Any(o => o.ModifierGroupId == required.ModifierGroupId))
            {
                throw new BusinessRuleException(ErrorCodes.RestoRequiredModifierMissing, "A required modifier was not selected.");
            }
        }

        return (options, groups);
    }

    /// <summary>Same rule as checkout: Owner/Admin/Manager direct; Cashier needs DiscountApply or approval.
    /// Kept as a copy in this service, matching the codebase's one-copy-per-action convention.</summary>
    private async Task<Guid?> EnsureDiscountAuthorizedAsync(
        Guid branchId, CheckoutDiscountInput discount, VoidSaleApprovalInput? approval, CancellationToken cancellationToken)
    {
        if (discount.Type == DiscountType.None)
        {
            return null;
        }

        if (_currentUser.Role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            return null;
        }

        if (_currentUser.Role != UserRole.Cashier)
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot apply a discount.");
        }

        if (await _grants.HasGrantAsync(_currentUser.UserId, UserPermission.DiscountApply, cancellationToken))
        {
            return null;
        }

        if (approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.DiscountApprovalRequired,
                "You don't have permission to apply a discount. An authorized Manager, Admin, or Owner must approve this item.");
        }

        return await _approverVerification.VerifyAsync(approval.ApproverEmail, approval.ApproverPassword, branchId, cancellationToken);
    }

    private async Task<bool> TracksInventoryAsync(Guid tenantId, Guid productVariantId, CancellationToken cancellationToken) =>
        await (
            from v in _db.ProductVariants.AsNoTracking()
            join p in _db.Products.AsNoTracking() on v.ProductId equals p.Id
            where v.TenantId == tenantId && v.Id == productVariantId
            select p.TrackInventory
        ).SingleAsync(cancellationToken);

    private static RestoOrderRound FindRound(RestoOrder order, Guid roundId) =>
        order.Rounds.SingleOrDefault(r => r.Id == roundId)
        ?? throw new NotFoundException(ErrorCodes.RestoRoundNotFound, "Round not found.");

    private static void RequireOpen(RestoOrder order)
    {
        if (order.Status != RestoOrderStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RestoOrderNotOpen, "This order is no longer open.");
        }
    }

    /// <summary>Bill-Out releases while the order is open and unpaid. PAYO releases only after settlement (spec 6.4).</summary>
    private static void RequireReleasable(RestoOrder order)
    {
        if (!IsUserReleasable(order))
        {
            throw new BusinessRuleException(ErrorCodes.RestoOrderNotOpen,
                order.ServiceType == RestoServiceType.PayAsYouOrder
                    ? "A Pay-as-you-order round is released only after the order is paid."
                    : "This order is no longer open.");
        }
    }

    private static bool IsUserReleasable(RestoOrder order) =>
        order.ServiceType == RestoServiceType.BillOut
            ? order.Status == RestoOrderStatus.Open
            : order.Status == RestoOrderStatus.Settled;

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
