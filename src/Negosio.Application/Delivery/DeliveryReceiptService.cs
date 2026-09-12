using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Reports;
using Negosio.Application.Settings;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

/// <summary>
/// Schedules, tracks and reports on one or more <see cref="DeliveryReceipt"/> records per
/// <see cref="Sale"/>. Every sold quantity is Take-now unless a <c>SaleItem.DeliveryRequiredQuantity</c>
/// was set at checkout; this service allocates that delivery-required portion across one or more dated,
/// partial delivery schedules. See the plan's Global Constraints for the concurrency and idempotency
/// strategy this class implements.
/// </summary>
public sealed class DeliveryReceiptService : IDeliveryReceiptService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateDeliveryReceiptRequest> _createValidator;
    private readonly IValidator<CreateDeliveryReceiptBatchRequest> _batchValidator;
    private readonly IValidator<CancelDeliveryReceiptRequest> _cancelValidator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IReceiptSettingsResolver _settingsResolver;
    private readonly TimeProvider _timeProvider;

    public DeliveryReceiptService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateDeliveryReceiptRequest> createValidator,
        IValidator<CreateDeliveryReceiptBatchRequest> batchValidator,
        IValidator<CancelDeliveryReceiptRequest> cancelValidator,
        IBranchAccessResolver branchAccess,
        IReceiptSettingsResolver settingsResolver,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _batchValidator = batchValidator;
        _cancelValidator = cancelValidator;
        _branchAccess = branchAccess;
        _settingsResolver = settingsResolver;
        _timeProvider = timeProvider;
    }

    public async Task<DeliveryReceiptDto> CreateAsync(Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        await _createValidator.ValidateAndThrowAppAsync(request, ct);

        var sale = await LoadDeliverableSaleAsync(tenantId, saleId, ct);
        EnsureNotPastBusinessToday(request.ScheduledDeliveryDate);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await LockSaleItemsAsync(tenantId, sale.Id, ct);

        var availability = await ComputeAvailabilityAsync(tenantId, sale, ct);
        var lines = ValidateAndResolveLines(sale, availability, request.Items);

        var preparedByName = await ResolvePreparedByNameAsync(ct);
        var sequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, ct);

        var dr = DeliveryReceipt.Create(
            tenantId, sale.BranchId, sale.Id, sale.SaleNumber, sequenceNumber, request.ScheduledDeliveryDate,
            request.RecipientName, request.DeliveryAddress, request.ContactNumber, request.DeliveryNotes,
            _currentUser.UserId, preparedByName);

        foreach (var (saleItem, quantity) in lines)
        {
            dr.AddItem(saleItem.Id, saleItem.ProductNameSnapshot, saleItem.VariantNameSnapshot, quantity, saleItem.UnitPrice);
        }

        _db.DeliveryReceipts.Add(dr);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "Another delivery was created for this sale at the same time. Please retry.");
        }

        await transaction.CommitAsync(ct);
        return await MapToDtoAsync(dr, ct);
    }

    public async Task<DeliveryReceiptBatchResultDto> CreateBatchAsync(
        Guid saleId, CreateDeliveryReceiptBatchRequest request, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        await _batchValidator.ValidateAndThrowAppAsync(request, ct);

        // Idempotency fast path — a retry arriving after the original batch already fully committed
        // returns the same rows without paying for a lock acquisition. This check alone is NOT
        // race-proof (it runs outside the transaction); the authoritative one is re-run below, after
        // the lock is held.
        if (await TryGetExistingBatchAsync(tenantId, saleId, request.BatchRequestId, ct) is { } alreadyApplied)
        {
            return alreadyApplied;
        }

        var sale = await LoadDeliverableSaleAsync(tenantId, saleId, ct);
        foreach (var schedule in request.Schedules)
        {
            EnsureNotPastBusinessToday(schedule.ScheduledDeliveryDate);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await LockSaleItemsAsync(tenantId, sale.Id, ct);

        // The race-proof idempotency check. A concurrent call with the same BatchRequestId may have
        // committed while this one was blocked on the lock above — in which case its rows are visible
        // only now. Re-checking here, with the lock held, is what actually makes batch creation
        // idempotent under concurrency; there is deliberately no unique index on BatchRequestId to fall
        // back on, since one batch legitimately writes N rows sharing that id (a unique index cannot
        // express that). Nothing has been written yet at this point, so the rollback is a no-op that
        // just releases the lock.
        if (await TryGetExistingBatchAsync(tenantId, sale.Id, request.BatchRequestId, ct) is { } wonByAnother)
        {
            await transaction.RollbackAsync(ct);
            return wonByAnother;
        }

        // One availability snapshot for the whole batch, decremented in-memory as each schedule is
        // resolved in order — this is what stops two schedules in the SAME batch from double-claiming
        // the same units (a per-schedule-only check would miss that, since neither schedule alone
        // exceeds availability).
        var availability = await ComputeAvailabilityAsync(tenantId, sale, ct);
        var preparedByName = await ResolvePreparedByNameAsync(ct);
        var nextSequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, ct);

        var created = new List<DeliveryReceipt>(request.Schedules.Count);
        foreach (var schedule in request.Schedules)
        {
            var lines = ValidateAndResolveLines(sale, availability, schedule.Items);

            var dr = DeliveryReceipt.Create(
                tenantId, sale.BranchId, sale.Id, sale.SaleNumber, nextSequenceNumber++, schedule.ScheduledDeliveryDate,
                schedule.RecipientName, schedule.DeliveryAddress, schedule.ContactNumber, schedule.DeliveryNotes,
                _currentUser.UserId, preparedByName, request.BatchRequestId);

            foreach (var (saleItem, quantity) in lines)
            {
                dr.AddItem(saleItem.Id, saleItem.ProductNameSnapshot, saleItem.VariantNameSnapshot, quantity, saleItem.UnitPrice);
                availability[saleItem.Id] -= quantity; // consume for the remaining schedules in this batch
            }

            _db.DeliveryReceipts.Add(dr);
            created.Add(dr);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        // Still reachable for a genuine (SaleId, SequenceNumber) race — e.g. a concurrent CreateAsync
        // and CreateBatchAsync on the same sale landing on an overlapping sequence number. A
        // BatchRequestId collision is no longer possible (that index is deliberately non-unique), so
        // there is no idempotent-recovery branch here any more.
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "Another delivery was created for this sale at the same time. Please retry.");
        }

        await transaction.CommitAsync(ct);

        var createdDtos = new List<DeliveryReceiptDto>(created.Count);
        foreach (var dr in created)
        {
            createdDtos.Add(await MapToDtoAsync(dr, ct));
        }

        return new DeliveryReceiptBatchResultDto(createdDtos, WasExistingBatch: false);
    }

    public async Task<IReadOnlyList<DeliveryReceiptDto>> ListForSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var sale = await _db.Sales.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        var receipts = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .OrderBy(d => d.SequenceNumber)
            .ToListAsync(ct);

        var dtos = new List<DeliveryReceiptDto>(receipts.Count);
        foreach (var dr in receipts)
        {
            dtos.Add(await MapToDtoAsync(dr, ct));
        }

        return dtos;
    }

    public async Task<DeliveryReceiptDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var dr = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.");

        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.", ct);
        return await MapToDtoAsync(dr, ct);
    }

    public Task<DeliveryReceiptDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public Task<DeliveryReceiptDto> CancelAsync(Guid id, CancelDeliveryReceiptRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public async Task<SaleDeliverySummaryDto> GetSaleFulfillmentAsync(Guid saleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        var availability = await ComputeAvailabilityAsync(tenantId, sale, ct);

        var itemDtos = sale.Items
            .Where(i => i.DeliveryRequiredQuantity > 0m)
            .OrderBy(i => i.CreatedAtUtc)
            .Select(i =>
            {
                var (pending, delivered) = availability.Raw.TryGetValue(i.Id, out var v) ? v : (0m, 0m);
                return new SaleItemFulfillmentDto(
                    i.Id, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, i.TakeNowQuantity,
                    i.DeliveryRequiredQuantity, pending, delivered, availability[i.Id]);
            })
            .ToList();

        var receipts = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .OrderBy(d => d.SequenceNumber)
            .ToListAsync(ct);

        var deliveryDtos = new List<DeliveryReceiptSummaryDto>(receipts.Count);
        foreach (var dr in receipts)
        {
            var itemsForDr = await MapItemsAsync(dr, ct);
            deliveryDtos.Add(new DeliveryReceiptSummaryDto(
                dr.Id, dr.SequenceNumber, dr.ScheduledDeliveryDate, dr.Status, dr.RecipientName, dr.DeliveryAddress,
                dr.ContactNumber, dr.DeliveryNotes, dr.DeliveredAtUtc, dr.CancelledAtUtc, dr.CancellationReason, itemsForDr));
        }

        var totalRequired = itemDtos.Sum(i => i.DeliveryRequiredQuantity);
        var totalPending = itemDtos.Sum(i => i.PendingQuantity);
        var totalDelivered = itemDtos.Sum(i => i.DeliveredQuantity);
        var totalAvailable = itemDtos.Sum(i => i.AvailableToScheduleQuantity);
        var todayLocal = BusinessToday();
        var hasOverduePending = receipts.Any(d => d.Status == DeliveryStatus.Pending && d.ScheduledDeliveryDate < todayLocal);

        var status = SaleFulfillmentCalculator.Derive(totalRequired, totalPending, totalDelivered, totalAvailable, hasOverduePending);

        return new SaleDeliverySummaryDto(
            sale.Id, status, sale.DeliveryCharge, itemDtos, deliveryDtos, CanCreateDelivery: totalAvailable > 0m);
    }

    // ---- shared helpers (also used by Task 10's MarkDeliveredAsync/CancelAsync) ----

    /// <summary>Returns the already-persisted result for <paramref name="batchRequestId"/> on
    /// <paramref name="saleId"/>, or null if that batch has not been applied yet. Called twice by
    /// <see cref="CreateBatchAsync"/>: once as a lock-free fast path, and once inside the transaction
    /// with the sale-items lock held — the latter is the call that actually makes batch creation
    /// idempotent under concurrency.
    /// <para>Scoped to <paramref name="saleId"/>, not just the tenant: a batch is always route-scoped to
    /// one sale, so a stale or reused BatchRequestId must never return a different sale's rows. This is
    /// also a security boundary — the fast path runs before <c>LoadDeliverableSaleAsync</c>, so without
    /// this clause a branch-scoped caller replaying another branch's BatchRequestId would receive that
    /// branch's delivery details without ever passing <see cref="GuardBranchAsync"/>.</para></summary>
    private async Task<DeliveryReceiptBatchResultDto?> TryGetExistingBatchAsync(
        Guid tenantId, Guid saleId, Guid batchRequestId, CancellationToken ct)
    {
        var existing = await _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId && d.BatchRequestId == batchRequestId)
            .OrderBy(d => d.SequenceNumber)
            .Include(d => d.Items)
            .ToListAsync(ct);
        if (existing.Count == 0)
        {
            return null;
        }

        var dtos = new List<DeliveryReceiptDto>(existing.Count);
        foreach (var dr in existing)
        {
            dtos.Add(await MapToDtoAsync(dr, ct));
        }

        return new DeliveryReceiptBatchResultDto(dtos, WasExistingBatch: true);
    }

    /// <summary>Per-SaleItem (Pending, Delivered) totals, indexable both as the raw tuple
    /// (<see cref="Raw"/>) and as the derived AvailableToScheduleQuantity via the indexer.</summary>
    private sealed class AvailabilityMap
    {
        private readonly Dictionary<Guid, decimal> _deliveryRequired;
        public Dictionary<Guid, (decimal Pending, decimal Delivered)> Raw { get; }

        public AvailabilityMap(Dictionary<Guid, decimal> deliveryRequired, Dictionary<Guid, (decimal Pending, decimal Delivered)> raw)
        {
            _deliveryRequired = deliveryRequired;
            Raw = raw;
        }

        public decimal this[Guid saleItemId]
        {
            get
            {
                var (pending, delivered) = Raw.TryGetValue(saleItemId, out var v) ? v : (0m, 0m);
                return _deliveryRequired.GetValueOrDefault(saleItemId) - pending - delivered;
            }
            set
            {
                // Only ever decremented in-memory during CreateBatchAsync — reassigns Pending so the
                // indexer's getter reflects the consumed amount for the NEXT schedule in the same batch.
                var deliveryRequired = _deliveryRequired.GetValueOrDefault(saleItemId);
                var (_, delivered) = Raw.TryGetValue(saleItemId, out var existing) ? existing : (0m, 0m);
                var impliedPending = deliveryRequired - delivered - value;
                Raw[saleItemId] = (impliedPending, delivered);
            }
        }
    }

    private async Task<AvailabilityMap> ComputeAvailabilityAsync(Guid tenantId, Sale sale, CancellationToken ct)
    {
        var saleItemIds = sale.Items.Select(i => i.Id).ToList();
        var allocations = await (
            from i in _db.DeliveryReceiptItems.AsNoTracking()
            join d in _db.DeliveryReceipts.AsNoTracking() on i.DeliveryReceiptId equals d.Id
            where i.TenantId == tenantId && saleItemIds.Contains(i.SaleItemId) && d.Status != DeliveryStatus.Cancelled
            select new { i.SaleItemId, i.Quantity, d.Status })
            .ToListAsync(ct);

        var raw = allocations
            .GroupBy(a => a.SaleItemId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Pending: g.Where(a => a.Status == DeliveryStatus.Pending).Sum(a => a.Quantity),
                    Delivered: g.Where(a => a.Status == DeliveryStatus.Delivered).Sum(a => a.Quantity)));

        var deliveryRequired = sale.Items.ToDictionary(i => i.Id, i => i.DeliveryRequiredQuantity);
        return new AvailabilityMap(deliveryRequired, raw);
    }

    /// <summary>
    /// Validates and resolves one delivery's requested lines against the (possibly already
    /// batch-decremented) <paramref name="availability"/> map — never trusts the frontend's own
    /// available-quantity math. Every SaleItemId must belong to <paramref name="sale"/> (same tenant
    /// and sale by construction, since <paramref name="sale"/>.Items is already tenant/sale-scoped).
    /// </summary>
    private static IReadOnlyList<(SaleItem SaleItem, decimal Quantity)> ValidateAndResolveLines(
        Sale sale, AvailabilityMap availability, IReadOnlyList<CreateDeliveryReceiptItemInput> items)
    {
        var pairs = new List<(SaleItem, decimal)>(items.Count);
        foreach (var line in items)
        {
            var saleItem = sale.Items.SingleOrDefault(i => i.Id == line.SaleItemId)
                ?? throw new BusinessRuleException(ErrorCodes.InvalidSaleItem, "A delivery line refers to an item that is not on this sale.");

            if (line.Quantity > availability[saleItem.Id])
            {
                throw new BusinessRuleException(
                    ErrorCodes.DeliveryQuantityExceedsAvailable,
                    $"Only {availability[saleItem.Id]} of \"{saleItem.ProductNameSnapshot}\" is still available to schedule.");
            }

            pairs.Add((saleItem, line.Quantity));
        }

        return pairs;
    }

    private async Task<Sale> LoadDeliverableSaleAsync(Guid tenantId, Guid saleId, CancellationToken ct)
    {
        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        if (sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded or SaleStatus.Refunded))
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotAllowed, "A delivery can only be scheduled for a completed sale.");
        }

        return sale;
    }

    /// <summary>
    /// Serializes concurrent allocation attempts against the same Sale's items. RowVersion (this
    /// codebase's default optimistic-concurrency idiom) only protects an UPDATE against a row that
    /// already exists — it cannot catch two concurrent CreateAsync/CreateBatchAsync calls each
    /// INSERTing a brand-new DeliveryReceiptItem, since neither call updates a shared row. Without
    /// this lock, two transactions under SQL Server's default READ COMMITTED isolation could both read
    /// the same "4 available" snapshot before either commits, and both insert — over-allocating past
    /// what was ever marked for delivery. <c>WITH (UPDLOCK, HOLDLOCK)</c> is the one pessimistic-lock
    /// precedent already in this codebase (<c>VoidSaleService</c>, for a different cross-aggregate
    /// race) — reused here for the same reason: it is the right tool exactly when RowVersion structurally
    /// cannot apply. The lock is held until the transaction commits or rolls back, so a second
    /// concurrent call blocks here until the first finishes, then re-reads a availability snapshot that
    /// correctly reflects what the first call just consumed.
    /// </summary>
    private async Task LockSaleItemsAsync(Guid tenantId, Guid saleId, CancellationToken ct) =>
        await _db.SaleItems
            .FromSqlInterpolated($"SELECT * FROM SaleItems WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = {tenantId} AND SaleId = {saleId}")
            .AsNoTracking()
            .ToListAsync(ct);

    private async Task<int> NextSequenceNumberAsync(Guid tenantId, Guid saleId, CancellationToken ct)
    {
        var max = await _db.DeliveryReceipts
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .Select(d => (int?)d.SequenceNumber)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private DateOnly BusinessToday() =>
        DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime + ReportPeriodResolver.BusinessOffset);

    private void EnsureNotPastBusinessToday(DateOnly scheduledDate)
    {
        if (scheduledDate < BusinessToday())
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryScheduleDateInPast, "The scheduled delivery date cannot be in the past.");
        }
    }

    private async Task<string> ResolvePreparedByNameAsync(CancellationToken ct) =>
        await _db.Users.Where(u => u.Id == _currentUser.UserId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

    private async Task<IReadOnlyList<DeliveryReceiptItemDto>> MapItemsAsync(DeliveryReceipt dr, CancellationToken ct)
    {
        var settings = await _settingsResolver.ResolveAsync(dr.BranchId, ct);
        return dr.Items
            .OrderBy(i => i.CreatedAtUtc)
            .ThenBy(i => i.Id)
            .Select(i =>
            {
                decimal? unitPrice = settings.DeliveryShowPrices ? i.UnitPrice : null;
                decimal? amount = settings.DeliveryShowPrices && i.UnitPrice is { } price
                    ? Money.Round(i.Quantity * price)
                    : null;
                return new DeliveryReceiptItemDto(i.SaleItemId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, unitPrice, amount);
            })
            .ToList();
    }

    /// <summary>Shared DTO builder for every read path (Create/CreateBatch's response, Get, List, and
    /// — once Task 10 lands — MarkDelivered/Cancel's response). Only <see cref="DeliveryReceipt.Items"/>
    /// needs to already be loaded on <paramref name="dr"/>; everything else (branch/business/settings,
    /// live DeliveryCharge, the delivered/cancelled actor names) is resolved here.</summary>
    private async Task<DeliveryReceiptDto> MapToDtoAsync(DeliveryReceipt dr, CancellationToken ct)
    {
        var settings = await _settingsResolver.ResolveAsync(dr.BranchId, ct);

        var profile = await _db.TenantProfiles.Where(p => p.Id == dr.TenantId)
            .Select(p => new { p.Name, p.ContactNumber, p.TaxId })
            .SingleAsync(ct);
        var branch = await _db.Branches.Where(b => b.Id == dr.BranchId)
            .Select(b => new { b.Name, b.AddressLine1, b.City, b.Province, b.ContactNumber })
            .FirstOrDefaultAsync(ct);

        var deliveryCharge = dr.SaleId is { } saleId
            ? await _db.Sales.AsNoTracking().Where(s => s.Id == saleId).Select(s => s.DeliveryCharge).FirstOrDefaultAsync(ct)
            : 0m;

        var actorIds = new[] { dr.DeliveredByUserId, dr.CancelledByUserId }.Where(id => id is not null).Select(id => id!.Value).ToList();
        var actorNames = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, ct);

        var addressParts = new[] { branch?.AddressLine1, branch?.City, branch?.Province }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();
        var businessAddress = addressParts.Length > 0 ? string.Join(", ", addressParts) : null;

        var items = dr.Items
            .OrderBy(i => i.CreatedAtUtc)
            .ThenBy(i => i.Id)
            .Select(i =>
            {
                decimal? unitPrice = settings.DeliveryShowPrices ? i.UnitPrice : null;
                decimal? amount = settings.DeliveryShowPrices && i.UnitPrice is { } price
                    ? Money.Round(i.Quantity * price)
                    : null;
                return new DeliveryReceiptItemDto(i.SaleItemId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, unitPrice, amount);
            })
            .ToList();

        return new DeliveryReceiptDto(
            dr.Id, dr.SaleId, dr.RelatedSaleNumber, dr.SequenceNumber, dr.ScheduledDeliveryDate, dr.Status, dr.CreatedAtUtc,
            branch?.Name ?? string.Empty, dr.RecipientName, dr.DeliveryAddress, dr.ContactNumber, dr.DeliveryNotes,
            dr.PreparedByNameSnapshot,
            dr.DeliveredAtUtc, dr.DeliveredByUserId is { } dbu ? actorNames.GetValueOrDefault(dbu) : null,
            dr.CancelledAtUtc, dr.CancelledByUserId is { } cbu ? actorNames.GetValueOrDefault(cbu) : null, dr.CancellationReason,
            items, deliveryCharge,
            HeaderText: settings.DeliveryHeaderText,
            FooterText: settings.DeliveryFooterText,
            BusinessName: profile.Name,
            BusinessAddress: businessAddress,
            BusinessContactNumber: branch?.ContactNumber ?? profile.ContactNumber,
            TaxId: profile.TaxId,
            ShowPrices: settings.DeliveryShowPrices,
            ShowRelatedSaleNumber: settings.DeliveryShowRelatedSaleNumber,
            ShowContactNumber: settings.DeliveryShowContactNumber,
            ShowSignatureFields: settings.DeliveryShowSignatureFields);
    }

    /// <summary>Branch-scoped users may only see their own branch's documents (404, not 403). Owner/Admin
    /// are unrestricted.</summary>
    private async Task GuardBranchAsync(Guid branchId, string code, string message, CancellationToken ct)
    {
        var assigned = await _branchAccess.AssignedBranchIdAsync(ct);
        if (assigned is { } scoped && scoped != branchId)
        {
            throw new NotFoundException(code, message);
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
