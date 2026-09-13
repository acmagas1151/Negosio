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
/// <see cref="Sale"/> — deliveries the business makes and pickups the customer collects, discriminated
/// by <see cref="DeliveryReceipt.Method"/> on the one shared table.
/// <para>Every sold quantity is Take-now unless checkout marked part of the line for delivery
/// (<c>SaleItem.DeliveryRequiredQuantity</c>) or pickup (<c>SaleItem.PickupRequiredQuantity</c>). Those two
/// intent pools are strictly separate: quantity marked for delivery can never be scheduled as a pickup
/// without an explicit, audited conversion, and take-now quantity can never be scheduled or converted at
/// all. See the plan's Global Constraints for the concurrency, idempotency and atomicity strategy this
/// class implements.</para>
/// </summary>
public sealed class DeliveryReceiptService : IDeliveryReceiptService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateDeliveryReceiptRequest> _createDeliveryValidator;
    private readonly IValidator<CreateDeliveryReceiptBatchRequest> _deliveryBatchValidator;
    private readonly IValidator<CreatePickupRequest> _createPickupValidator;
    private readonly IValidator<CreatePickupBatchRequest> _pickupBatchValidator;
    private readonly IValidator<CancelDeliveryRequest> _cancelDeliveryValidator;
    private readonly IValidator<CancelPickupRequest> _cancelPickupValidator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IReceiptSettingsResolver _settingsResolver;
    private readonly TimeProvider _timeProvider;

    public DeliveryReceiptService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateDeliveryReceiptRequest> createDeliveryValidator,
        IValidator<CreateDeliveryReceiptBatchRequest> deliveryBatchValidator,
        IValidator<CreatePickupRequest> createPickupValidator,
        IValidator<CreatePickupBatchRequest> pickupBatchValidator,
        IValidator<CancelDeliveryRequest> cancelDeliveryValidator,
        IValidator<CancelPickupRequest> cancelPickupValidator,
        IBranchAccessResolver branchAccess,
        IReceiptSettingsResolver settingsResolver,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _createDeliveryValidator = createDeliveryValidator;
        _deliveryBatchValidator = deliveryBatchValidator;
        _createPickupValidator = createPickupValidator;
        _pickupBatchValidator = pickupBatchValidator;
        _cancelDeliveryValidator = cancelDeliveryValidator;
        _cancelPickupValidator = cancelPickupValidator;
        _branchAccess = branchAccess;
        _settingsResolver = settingsResolver;
        _timeProvider = timeProvider;
    }

    // ---- Create ----------------------------------------------------------------------------------
    //
    // The four public create methods are thin adapters: each validates its own request type, normalizes
    // it into the method-agnostic ScheduleInput shape, and calls the one shared allocation path. All the
    // ordering that makes allocation correct (transaction -> sale-items lock -> post-lock idempotency
    // re-check -> availability snapshot -> build -> single save) lives exactly once, in those two private
    // methods, and is unchanged from the shipped delivery-only implementation.

    public async Task<FulfillmentScheduleDto> CreateDeliveryAsync(
        Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default)
    {
        await _createDeliveryValidator.ValidateAndThrowAppAsync(request, ct);
        return await CreateScheduleAsync(saleId, FulfillmentMethod.Delivery, ScheduleInput.From(request), ct);
    }

    public async Task<FulfillmentBatchResultDto> CreateDeliveryBatchAsync(
        Guid saleId, CreateDeliveryReceiptBatchRequest request, CancellationToken ct = default)
    {
        await _deliveryBatchValidator.ValidateAndThrowAppAsync(request, ct);
        return await CreateScheduleBatchAsync(
            saleId, FulfillmentMethod.Delivery, request.BatchRequestId,
            request.Schedules.Select(ScheduleInput.From).ToList(), ct);
    }

    public async Task<FulfillmentScheduleDto> CreatePickupAsync(
        Guid saleId, CreatePickupRequest request, CancellationToken ct = default)
    {
        await _createPickupValidator.ValidateAndThrowAppAsync(request, ct);
        return await CreateScheduleAsync(saleId, FulfillmentMethod.Pickup, ScheduleInput.From(request), ct);
    }

    public async Task<FulfillmentBatchResultDto> CreatePickupBatchAsync(
        Guid saleId, CreatePickupBatchRequest request, CancellationToken ct = default)
    {
        await _pickupBatchValidator.ValidateAndThrowAppAsync(request, ct);
        return await CreateScheduleBatchAsync(
            saleId, FulfillmentMethod.Pickup, request.BatchRequestId,
            request.Schedules.Select(ScheduleInput.From).ToList(), ct);
    }

    /// <summary>One schedule of either method, normalized off the two request shapes. A pickup carries no
    /// <see cref="DeliveryAddress"/>.</summary>
    private sealed record ScheduleInput(
        DateOnly ScheduledDate,
        string RecipientName,
        string? DeliveryAddress,
        string? ContactNumber,
        string? Notes,
        IReadOnlyList<FulfillmentItemInput> Items)
    {
        public static ScheduleInput From(CreateDeliveryReceiptRequest r) =>
            new(r.ScheduledDate, r.RecipientName, r.DeliveryAddress, r.ContactNumber, r.Notes, r.Items);

        public static ScheduleInput From(CreatePickupRequest r) =>
            new(r.ScheduledDate, r.RecipientName, null, r.ContactNumber, r.Notes, r.Items);
    }

    private async Task<FulfillmentScheduleDto> CreateScheduleAsync(
        Guid saleId, FulfillmentMethod method, ScheduleInput schedule, CancellationToken ct)
    {
        var tenantId = RequireTenant();

        var sale = await LoadFulfillableSaleAsync(tenantId, saleId, ct);
        EnsureNotPastBusinessToday(schedule.ScheduledDate, method);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Use the rows the lock itself returned, never the pre-lock copy on `sale`. The intent columns
        // are mutable now (a cancellation-with-conversion moves quantity between the two pools), so a
        // snapshot taken before the lock can already be stale by the time we hold it.
        var lockedItems = await LockSaleItemsAsync(tenantId, sale.Id, ct);

        var availability = await ComputeAvailabilityAsync(tenantId, lockedItems, ct);
        var lines = ValidateAndResolveLines(lockedItems, availability, method, schedule.Items);

        var preparedByName = await ResolvePreparedByNameAsync(ct);
        var sequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, method, ct);

        var dr = BuildSchedule(tenantId, sale, method, sequenceNumber, schedule, preparedByName, batchRequestId: null);

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
                "Another schedule was created for this sale at the same time. Please retry.");
        }

        await transaction.CommitAsync(ct);
        return await MapToDtoAsync(dr, ct);
    }

    private async Task<FulfillmentBatchResultDto> CreateScheduleBatchAsync(
        Guid saleId, FulfillmentMethod method, Guid batchRequestId, IReadOnlyList<ScheduleInput> schedules, CancellationToken ct)
    {
        var tenantId = RequireTenant();

        // Branch/tenant/sale-status guard runs first, before any idempotency shortcut — a
        // branch-scoped caller must never be able to read another branch's schedule rows just by
        // replaying a BatchRequestId it happens to know, even for a genuine sale id.
        var sale = await LoadFulfillableSaleAsync(tenantId, saleId, ct);

        // Idempotency fast path — a retry arriving after the original batch already fully committed
        // returns the same rows without paying for a lock acquisition. This check alone is NOT
        // race-proof (it runs outside the transaction); the authoritative one is re-run below, after
        // the lock is held. Deliberately placed after LoadFulfillableSaleAsync (not before) so the
        // branch guard above always runs first — see TryGetExistingBatchAsync's doc comment.
        if (await TryGetExistingBatchAsync(tenantId, sale.Id, method, batchRequestId, ct) is { } alreadyApplied)
        {
            return alreadyApplied;
        }

        foreach (var schedule in schedules)
        {
            EnsureNotPastBusinessToday(schedule.ScheduledDate, method);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // As in CreateScheduleAsync: the locked rows, not the pre-lock copy on `sale`, are the
        // authoritative intent snapshot.
        var lockedItems = await LockSaleItemsAsync(tenantId, sale.Id, ct);

        // The race-proof idempotency check. A concurrent call with the same BatchRequestId may have
        // committed while this one was blocked on the lock above — in which case its rows are visible
        // only now. Re-checking here, with the lock held, is what actually makes batch creation
        // idempotent under concurrency; there is deliberately no unique index on BatchRequestId to fall
        // back on, since one batch legitimately writes N rows sharing that id (a unique index cannot
        // express that). Nothing has been written yet at this point, so the rollback is a no-op that
        // just releases the lock.
        if (await TryGetExistingBatchAsync(tenantId, sale.Id, method, batchRequestId, ct) is { } wonByAnother)
        {
            await transaction.RollbackAsync(ct);
            return wonByAnother;
        }

        // One availability snapshot for the whole batch, decremented in-memory as each schedule is
        // resolved in order — this is what stops two schedules in the SAME batch from double-claiming
        // the same units (a per-schedule-only check would miss that, since neither schedule alone
        // exceeds availability).
        var availability = await ComputeAvailabilityAsync(tenantId, lockedItems, ct);
        var preparedByName = await ResolvePreparedByNameAsync(ct);
        var nextSequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, method, ct);

        var created = new List<DeliveryReceipt>(schedules.Count);
        foreach (var schedule in schedules)
        {
            var lines = ValidateAndResolveLines(lockedItems, availability, method, schedule.Items);

            var dr = BuildSchedule(tenantId, sale, method, nextSequenceNumber++, schedule, preparedByName, batchRequestId);

            foreach (var (saleItem, quantity) in lines)
            {
                dr.AddItem(saleItem.Id, saleItem.ProductNameSnapshot, saleItem.VariantNameSnapshot, quantity, saleItem.UnitPrice);
                availability.Consume(saleItem.Id, method, quantity); // consume for the remaining schedules in this batch
            }

            _db.DeliveryReceipts.Add(dr);
            created.Add(dr);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        // Still reachable for a genuine (SaleId, Method, SequenceNumber) race — e.g. a concurrent
        // single-create and batch-create on the same sale and method landing on an overlapping sequence
        // number. A BatchRequestId collision is no longer possible (that index is deliberately
        // non-unique), so there is no idempotent-recovery branch here any more.
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "Another schedule was created for this sale at the same time. Please retry.");
        }

        await transaction.CommitAsync(ct);

        var createdDtos = new List<FulfillmentScheduleDto>(created.Count);
        foreach (var dr in created)
        {
            createdDtos.Add(await MapToDtoAsync(dr, ct));
        }

        return new FulfillmentBatchResultDto(createdDtos, WasExistingBatch: false);
    }

    private DeliveryReceipt BuildSchedule(
        Guid tenantId, Sale sale, FulfillmentMethod method, int sequenceNumber,
        ScheduleInput schedule, string preparedByName, Guid? batchRequestId) =>
        method == FulfillmentMethod.Pickup
            ? DeliveryReceipt.CreatePickup(
                tenantId, sale.BranchId, sale.Id, sale.SaleNumber, sequenceNumber, schedule.ScheduledDate,
                schedule.RecipientName, schedule.ContactNumber, schedule.Notes,
                _currentUser.UserId, preparedByName, batchRequestId)
            : DeliveryReceipt.CreateDelivery(
                tenantId, sale.BranchId, sale.Id, sale.SaleNumber, sequenceNumber, schedule.ScheduledDate,
                schedule.RecipientName, schedule.DeliveryAddress!, schedule.ContactNumber, schedule.Notes,
                _currentUser.UserId, preparedByName, batchRequestId);

    // ---- Read ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<FulfillmentScheduleDto>> ListForSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var sale = await _db.Sales.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        var receipts = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .OrderBy(d => d.Method)
            .ThenBy(d => d.SequenceNumber)
            .ToListAsync(ct);

        var dtos = new List<FulfillmentScheduleDto>(receipts.Count);
        foreach (var dr in receipts)
        {
            dtos.Add(await MapToDtoAsync(dr, ct));
        }

        return dtos;
    }

    /// <summary>Serves both methods — the id is unique across the shared table and the DTO carries
    /// <see cref="FulfillmentScheduleDto.Method"/>.</summary>
    public async Task<FulfillmentScheduleDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var dr = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.");

        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.", ct);
        return await MapToDtoAsync(dr, ct);
    }

    public async Task<SaleFulfillmentSummaryDto> GetSaleFulfillmentAsync(Guid saleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        var availability = await ComputeAvailabilityAsync(tenantId, sale.Items, ct);

        // Every line with ANY post-checkout intent, not delivery alone. A line that is entirely take-now
        // has nothing to track and is deliberately omitted — that is what makes a wholly take-now sale
        // derive as NotApplicable below.
        var itemDtos = sale.Items
            .Where(i => i.DeliveryRequiredQuantity > 0m || i.PickupRequiredQuantity > 0m)
            .OrderBy(i => i.CreatedAtUtc)
            .ThenBy(i => i.Id)
            .Select(i =>
            {
                var delivery = availability.TotalsFor(i.Id, FulfillmentMethod.Delivery);
                var pickup = availability.TotalsFor(i.Id, FulfillmentMethod.Pickup);
                return new SaleItemFulfillmentDto(
                    i.Id, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, i.TakeNowQuantity,
                    DeliveryUnscheduledQuantity: availability.Available(i.Id, FulfillmentMethod.Delivery),
                    DeliveryPendingQuantity: delivery.Pending,
                    DeliveredQuantity: delivery.Completed,
                    PickupUnscheduledQuantity: availability.Available(i.Id, FulfillmentMethod.Pickup),
                    PickupPendingQuantity: pickup.Pending,
                    ClaimedQuantity: pickup.Completed);
            })
            .ToList();

        var schedules = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .OrderBy(d => d.Method)
            .ThenBy(d => d.SequenceNumber)
            .ToListAsync(ct);

        var deliveries = new List<FulfillmentScheduleDto>();
        var pickups = new List<FulfillmentScheduleDto>();
        foreach (var dr in schedules)
        {
            var dto = await MapToDtoAsync(dr, ct);
            (dr.Method == FulfillmentMethod.Pickup ? pickups : deliveries).Add(dto);
        }

        var conversions = await LoadConversionsAsync(tenantId, sale, ct);

        var todayLocal = BusinessToday();
        var hasOverduePending = schedules.Any(d => d.Status == FulfillmentStatus.Pending && d.ScheduledDate < todayLocal);

        var status = SaleFulfillmentCalculator.Derive(
            soldQuantity: itemDtos.Sum(i => i.Quantity),
            takeNowQuantity: itemDtos.Sum(i => i.TakeNowQuantity),
            deliveredQuantity: itemDtos.Sum(i => i.DeliveredQuantity),
            claimedQuantity: itemDtos.Sum(i => i.ClaimedQuantity),
            deliveryPendingQuantity: itemDtos.Sum(i => i.DeliveryPendingQuantity),
            pickupPendingQuantity: itemDtos.Sum(i => i.PickupPendingQuantity),
            deliveryUnscheduledQuantity: itemDtos.Sum(i => i.DeliveryUnscheduledQuantity),
            pickupUnscheduledQuantity: itemDtos.Sum(i => i.PickupUnscheduledQuantity),
            hasOverduePendingSchedule: hasOverduePending);

        return new SaleFulfillmentSummaryDto(
            sale.Id, status, sale.DeliveryCharge, itemDtos, deliveries, pickups, conversions,
            CanCreateDelivery: itemDtos.Any(i => i.DeliveryUnscheduledQuantity > 0m),
            CanCreatePickup: itemDtos.Any(i => i.PickupUnscheduledQuantity > 0m));
    }

    // ---- Completion ------------------------------------------------------------------------------

    public Task<FulfillmentScheduleDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default) =>
        CompleteAsync(id, FulfillmentMethod.Delivery, ct);

    public Task<FulfillmentScheduleDto> MarkClaimedAsync(Guid id, CancellationToken ct = default) =>
        CompleteAsync(id, FulfillmentMethod.Pickup, ct);

    /// <summary>
    /// The one status transition shared by <c>/deliver</c> and <c>/claim</c>. The two verbs stay separate
    /// on purpose: each rejects the other method outright, so "mark this delivery claimed" is an error
    /// rather than a silently-accepted no-op. Claiming is a COMPLETION and never a cancellation — a
    /// claimed pickup carries no cancellation metadata at all.
    /// </summary>
    private async Task<FulfillmentScheduleDto> CompleteAsync(Guid id, FulfillmentMethod expectedMethod, CancellationToken ct)
    {
        var tenantId = RequireTenant();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var dr = await _db.DeliveryReceipts.Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.");
        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.", ct);

        EnsureMethod(dr, expectedMethod);

        if (dr.Status != FulfillmentStatus.Pending)
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotPending,
                expectedMethod == FulfillmentMethod.Pickup
                    ? "Only a pending pickup can be marked claimed."
                    : "Only a pending delivery can be marked delivered.");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (expectedMethod == FulfillmentMethod.Pickup)
        {
            dr.MarkClaimed(_currentUser.UserId, nowUtc);
        }
        else
        {
            dr.MarkDelivered(_currentUser.UserId, nowUtc);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another status change (a race from a second tab/user) committed between our load and
            // our write — roll back, then report a conflict rather than silently overwriting it.
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This schedule was changed by someone else. Please refresh and try again.");
        }

        await transaction.CommitAsync(ct);
        return await MapToDtoAsync(dr, ct);
    }

    // ---- Cancellation with disposition -----------------------------------------------------------

    /// <summary>Builds the replacement schedule a disposition creates. Takes the sequence number as an
    /// argument because <see cref="DeliveryReceipt"/> fixes it at construction and it has to be the next
    /// one for the TARGET method, which only the shared cancel path knows.</summary>
    private delegate DeliveryReceipt ReplacementFactory(Sale sale, int sequenceNumber, string preparedByName);

    public async Task<CancellationResultDto> CancelDeliveryAsync(
        Guid id, CancelDeliveryRequest request, CancellationToken ct = default)
    {
        RequireTenant();
        await _cancelDeliveryValidator.ValidateAndThrowAppAsync(request, ct);

        switch (request.Disposition)
        {
            case CancellationDisposition.DeliverLater:
                RejectUnwantedReplacement(request.Replacement, request.Disposition);
                return await CancelWithDispositionAsync(
                    id, FulfillmentMethod.Delivery, request.Reason, request.Disposition,
                    convertToMethod: FulfillmentMethod.Delivery,
                    completeReplacementImmediately: false,
                    buildReplacement: null, ct);

            case CancellationDisposition.ConvertToPickup:
            {
                var replacement = RequireReplacement(request.Replacement);
                // A genuine future pickup — the same date rule every new schedule obeys.
                EnsureNotPastBusinessToday(replacement.ScheduledDate, FulfillmentMethod.Pickup);
                return await CancelWithDispositionAsync(
                    id, FulfillmentMethod.Delivery, request.Reason, request.Disposition,
                    convertToMethod: FulfillmentMethod.Pickup,
                    completeReplacementImmediately: false,
                    buildReplacement: PickupReplacementFactory(replacement), ct);
            }

            case CancellationDisposition.CustomerPickedUpInstead:
            {
                var replacement = RequireReplacement(request.Replacement);
                // Deliberately NOT date-validated: the collection ALREADY happened, so a date at or
                // before business-today is the correct input here, not an error.
                return await CancelWithDispositionAsync(
                    id, FulfillmentMethod.Delivery, request.Reason, request.Disposition,
                    convertToMethod: FulfillmentMethod.Pickup,
                    completeReplacementImmediately: true,
                    buildReplacement: PickupReplacementFactory(replacement), ct);
            }

            default:
                throw new BusinessRuleException(ErrorCodes.InvalidCancellationDisposition,
                    $"{request.Disposition} is not a valid disposition for a delivery.");
        }
    }

    public async Task<CancellationResultDto> CancelPickupAsync(
        Guid id, CancelPickupRequest request, CancellationToken ct = default)
    {
        RequireTenant();
        await _cancelPickupValidator.ValidateAndThrowAppAsync(request, ct);

        switch (request.Disposition)
        {
            case CancellationDisposition.PickupLater:
                RejectUnwantedReplacement(request.Replacement, request.Disposition);
                return await CancelWithDispositionAsync(
                    id, FulfillmentMethod.Pickup, request.Reason, request.Disposition,
                    convertToMethod: FulfillmentMethod.Pickup,
                    completeReplacementImmediately: false,
                    buildReplacement: null, ct);

            case CancellationDisposition.ConvertToDelivery:
            {
                var replacement = RequireReplacement(request.Replacement);
                EnsureNotPastBusinessToday(replacement.ScheduledDate, FulfillmentMethod.Delivery);
                return await CancelWithDispositionAsync(
                    id, FulfillmentMethod.Pickup, request.Reason, request.Disposition,
                    convertToMethod: FulfillmentMethod.Delivery,
                    completeReplacementImmediately: false,
                    buildReplacement: DeliveryReplacementFactory(replacement), ct);
            }

            default:
                throw new BusinessRuleException(ErrorCodes.InvalidCancellationDisposition,
                    $"{request.Disposition} is not a valid disposition for a pickup.");
        }
    }

    private ReplacementFactory PickupReplacementFactory(PickupReplacementInput input) =>
        (sale, sequenceNumber, preparedByName) => DeliveryReceipt.CreatePickup(
            sale.TenantId, sale.BranchId, sale.Id, sale.SaleNumber, sequenceNumber, input.ScheduledDate,
            input.RecipientName, input.ContactNumber, input.Notes, _currentUser.UserId, preparedByName);

    private ReplacementFactory DeliveryReplacementFactory(DeliveryReplacementInput input) =>
        (sale, sequenceNumber, preparedByName) => DeliveryReceipt.CreateDelivery(
            sale.TenantId, sale.BranchId, sale.Id, sale.SaleNumber, sequenceNumber, input.ScheduledDate,
            input.RecipientName, input.DeliveryAddress, input.ContactNumber, input.Notes,
            _currentUser.UserId, preparedByName);

    private static T RequireReplacement<T>(T? replacement) where T : class =>
        replacement ?? throw new BusinessRuleException(
            ErrorCodes.ReplacementDetailsRequired, "Replacement schedule details are required for this disposition.");

    private static void RejectUnwantedReplacement(object? replacement, CancellationDisposition disposition)
    {
        if (replacement is not null)
        {
            throw new BusinessRuleException(
                ErrorCodes.InvalidCancellationDisposition,
                $"{disposition} releases the quantity back to unscheduled and must not carry replacement details.");
        }
    }

    /// <summary>
    /// Cancels a pending schedule and applies its disposition — all inside one transaction, with the
    /// sale-items lock held, so the cancellation, the intent conversion, the audit event and any
    /// replacement schedule either all commit or none do.
    /// <para>Cancelling never touches the Sale or its DeliveryCharge, and this schedule's own item rows
    /// are left exactly as they are — they stay forever as audit history. Quantities are "released" only
    /// in the sense that <see cref="ComputeAvailabilityAsync"/> excludes Cancelled rows from its
    /// Pending/Completed sums, so a future create sees them as available again automatically.</para>
    /// </summary>
    private async Task<CancellationResultDto> CancelWithDispositionAsync(
        Guid id,
        FulfillmentMethod expectedMethod,
        string reason,
        CancellationDisposition disposition,
        FulfillmentMethod convertToMethod,      // == expectedMethod when the quantities just go back to unscheduled
        bool completeReplacementImmediately,    // true only for CustomerPickedUpInstead
        ReplacementFactory? buildReplacement,   // null when there is no replacement
        CancellationToken ct)
    {
        var tenantId = RequireTenant();

        // Tracked: dr.Cancel mutates it, and its RowVersion is the optimistic-concurrency token.
        var dr = await _db.DeliveryReceipts.Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.");
        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.", ct);

        EnsureMethod(dr, expectedMethod);

        // This is what makes a Delivered delivery and a Claimed pickup un-cancellable and
        // un-convertible: both are terminal, and a terminal outcome is never rewritten.
        if (dr.Status != FulfillmentStatus.Pending)
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotPending,
                expectedMethod == FulfillmentMethod.Pickup
                    ? "Only a pending pickup can be cancelled."
                    : "Only a pending delivery can be cancelled.");
        }

        if (dr.SaleId is not { } saleId)
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotAllowed, "This schedule is not linked to a sale and has no quantities to release.");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // The same lock every allocating path takes, so a conversion can never race an allocation.
        await LockSaleItemsAsync(tenantId, saleId, ct);

        // A concurrent mark-delivered / claim / cancel may have committed while we waited on the lock —
        // the status we checked above is a pre-lock read and can already be stale.
        var currentStatus = await _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.Id == dr.Id)
            .Select(d => d.Status)
            .SingleAsync(ct);
        if (currentStatus != FulfillmentStatus.Pending)
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This schedule was changed by someone else. Please refresh and try again.");
        }

        // A disposition that builds a replacement is creating a brand-new schedule, so it must clear the
        // SAME sale-status gate CreateScheduleAsync does. Without this, a sale voided while one of its
        // schedules was still Pending could gain a fresh Pending schedule through the cancel-with-conversion
        // back door — a schedule the create endpoints would have refused outright.
        // The two release-only dispositions (DeliverLater / PickupLater) are deliberately NOT gated: they
        // create nothing, and the quantity they release back to unscheduled can never actually be used,
        // because creating a schedule against a voided sale is already blocked.
        Sale sale;
        if (buildReplacement is not null)
        {
            sale = await LoadFulfillableSaleAsync(tenantId, saleId, ct);
        }
        else
        {
            sale = await _db.Sales.AsNoTracking()
                .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
                ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        }

        // TRACKED on purpose — ConvertFulfillment mutates these rows, and it is the only mutator either
        // intent column has after checkout.
        var saleItems = await _db.SaleItems
            .Where(i => i.TenantId == tenantId && i.SaleId == saleId)
            .ToListAsync(ct);

        // The domain re-validates the method/disposition pairing; take-now is never accepted.
        dr.Cancel(_currentUser.UserId, reason, disposition, nowUtc);

        DeliveryReceipt? replacement = null;
        if (buildReplacement is not null)
        {
            var preparedByName = await ResolvePreparedByNameAsync(ct);
            var sequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, convertToMethod, ct);
            replacement = buildReplacement(sale, sequenceNumber, preparedByName);

            foreach (var item in dr.Items.OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id))
            {
                // Same SaleItemId, same quantity, same snapshots — the replacement carries exactly what
                // the cancelled schedule held, never a recomputed figure.
                replacement.AddItem(
                    item.SaleItemId, item.ProductNameSnapshot, item.VariantNameSnapshot, item.Quantity, item.UnitPrice);
            }

            if (completeReplacementImmediately)
            {
                // CustomerPickedUpInstead only: the collection already happened, so the replacement is
                // born Completed. The cancelled DELIVERY stays Cancelled — it is never marked Delivered,
                // and no take-now adjustment is created anywhere on this path.
                replacement.MarkClaimed(_currentUser.UserId, nowUtc);
            }

            _db.DeliveryReceipts.Add(replacement);
        }

        foreach (var item in dr.Items)
        {
            if (convertToMethod != expectedMethod)
            {
                var saleItem = saleItems.SingleOrDefault(i => i.Id == item.SaleItemId)
                    ?? throw new BusinessRuleException(
                        ErrorCodes.InvalidSaleItem, "This schedule refers to an item that is no longer on its sale.");
                saleItem.ConvertFulfillment(expectedMethod, convertToMethod, item.Quantity);
            }

            // Written either way. A same-method event (a plain release) is still recorded — the audit
            // trail should show WHY quantity returned to unscheduled, not just that it did. Replaying
            // this log backwards over the current intent columns is what reconstructs the original
            // checkout allocation.
            _db.FulfillmentConversions.Add(FulfillmentConversion.Record(
                tenantId, saleId, item.SaleItemId, item.Quantity, expectedMethod, convertToMethod,
                sourceRecordId: dr.Id, replacementRecordId: replacement?.Id,
                reason, _currentUser.UserId, nowUtc));
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This schedule was changed by someone else. Please refresh and try again.");
        }
        // The unique filtered index on FulfillmentConversions (SourceRecordId, SaleItemId) is the
        // retry-safety mechanism: a retried cancellation that would re-run the same conversion collides
        // here and rolls the WHOLE transaction back, rather than converting the quantity twice.
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This schedule was already cancelled by someone else. Please refresh and try again.");
        }

        await transaction.CommitAsync(ct);

        var cancelledDto = await MapToDtoAsync(dr, ct);
        var replacementDto = replacement is null ? null : await MapToDtoAsync(replacement, ct);
        return new CancellationResultDto(cancelledDto, replacementDto);
    }

    // ---- Shared helpers --------------------------------------------------------------------------

    /// <summary>Returns the already-persisted result for <paramref name="batchRequestId"/> on
    /// <paramref name="saleId"/> and <paramref name="method"/>, or null if that batch has not been applied
    /// yet. Called twice by <see cref="CreateScheduleBatchAsync"/>: once as a lock-free fast path, and once
    /// inside the transaction with the sale-items lock held — the latter is the call that actually makes
    /// batch creation idempotent under concurrency.
    /// <para>Scoped to <paramref name="saleId"/> and <paramref name="method"/>, not just the tenant: a batch
    /// is always route-scoped to one sale and one method, so a stale or reused BatchRequestId must never
    /// return a different sale's — or the other method's — rows. Both call sites run AFTER
    /// <c>LoadFulfillableSaleAsync</c>, which already enforces tenant+branch+sale-status via
    /// <see cref="GuardBranchAsync"/> — so even a caller who knows a specific out-of-branch
    /// <paramref name="saleId"/> and its exact BatchRequestId cannot reach this fast path without first
    /// clearing the branch guard.</para></summary>
    private async Task<FulfillmentBatchResultDto?> TryGetExistingBatchAsync(
        Guid tenantId, Guid saleId, FulfillmentMethod method, Guid batchRequestId, CancellationToken ct)
    {
        var existing = await _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId && d.Method == method && d.BatchRequestId == batchRequestId)
            .OrderBy(d => d.SequenceNumber)
            .Include(d => d.Items)
            .ToListAsync(ct);
        if (existing.Count == 0)
        {
            return null;
        }

        var dtos = new List<FulfillmentScheduleDto>(existing.Count);
        foreach (var dr in existing)
        {
            dtos.Add(await MapToDtoAsync(dr, ct));
        }

        return new FulfillmentBatchResultDto(dtos, WasExistingBatch: true);
    }

    private sealed record MethodTotals(decimal Pending, decimal Completed);

    /// <summary>Per-SaleItem, PER-METHOD (Pending, Completed) totals. Delivery and Pickup are two entirely
    /// separate pools — each method's schedules are only ever checked against that method's own intent
    /// column, which is what stops delivery-marked quantity from being scheduled as a pickup (or the
    /// reverse) without an explicit conversion.</summary>
    private sealed class AvailabilityMap
    {
        private readonly Dictionary<Guid, (decimal Delivery, decimal Pickup)> _intent;
        public Dictionary<(Guid SaleItemId, FulfillmentMethod Method), MethodTotals> Raw { get; }

        public AvailabilityMap(
            Dictionary<Guid, (decimal Delivery, decimal Pickup)> intent,
            Dictionary<(Guid, FulfillmentMethod), MethodTotals> raw)
        {
            _intent = intent;
            Raw = raw;
        }

        public MethodTotals TotalsFor(Guid saleItemId, FulfillmentMethod method) =>
            Raw.TryGetValue((saleItemId, method), out var v) ? v : new MethodTotals(0m, 0m);

        /// <summary>Quantity of <paramref name="saleItemId"/> still available to put on a NEW schedule
        /// of <paramref name="method"/>: that method's intent minus what its own pending and completed
        /// schedules already hold. Cancelled schedules are excluded upstream, which is exactly how a
        /// cancellation releases quantity.</summary>
        public decimal Available(Guid saleItemId, FulfillmentMethod method)
        {
            var intent = _intent.TryGetValue(saleItemId, out var i) ? i : (Delivery: 0m, Pickup: 0m);
            var methodIntent = method == FulfillmentMethod.Delivery ? intent.Delivery : intent.Pickup;
            var totals = TotalsFor(saleItemId, method);
            return methodIntent - totals.Pending - totals.Completed;
        }

        /// <summary>In-memory consumption during a multi-schedule batch, so two schedules in the same
        /// request cannot both claim the same units. Never persisted.</summary>
        public void Consume(Guid saleItemId, FulfillmentMethod method, decimal quantity)
        {
            var totals = TotalsFor(saleItemId, method);
            Raw[(saleItemId, method)] = totals with { Pending = totals.Pending + quantity };
        }
    }

    /// <summary>Builds the map from a specific set of sale lines. Allocating callers pass the rows
    /// <see cref="LockSaleItemsAsync"/> just returned — the only intent snapshot that is guaranteed
    /// current under the lock; read-only callers pass the sale's own loaded lines.</summary>
    private async Task<AvailabilityMap> ComputeAvailabilityAsync(
        Guid tenantId, IReadOnlyCollection<SaleItem> saleItems, CancellationToken ct)
    {
        var saleItemIds = saleItems.Select(i => i.Id).ToList();
        var allocations = await (
            from i in _db.DeliveryReceiptItems.AsNoTracking()
            join d in _db.DeliveryReceipts.AsNoTracking() on i.DeliveryReceiptId equals d.Id
            where i.TenantId == tenantId && saleItemIds.Contains(i.SaleItemId)
                  && d.Status != FulfillmentStatus.Cancelled
            select new { i.SaleItemId, i.Quantity, d.Method, d.Status })
            .ToListAsync(ct);

        var raw = allocations
            .GroupBy(a => (a.SaleItemId, a.Method))
            .ToDictionary(
                g => g.Key,
                g => new MethodTotals(
                    Pending: g.Where(a => a.Status == FulfillmentStatus.Pending).Sum(a => a.Quantity),
                    Completed: g.Where(a => a.Status == FulfillmentStatus.Completed).Sum(a => a.Quantity)));

        var intent = saleItems.ToDictionary(
            i => i.Id, i => (Delivery: i.DeliveryRequiredQuantity, Pickup: i.PickupRequiredQuantity));

        return new AvailabilityMap(intent, raw);
    }

    /// <summary>
    /// Validates and resolves one schedule's requested lines against the (possibly already
    /// batch-decremented) <paramref name="availability"/> map for <paramref name="method"/> — never trusts
    /// the frontend's own available-quantity math. Every SaleItemId must belong to
    /// <paramref name="saleItems"/> (same tenant and sale by construction, since that set is already
    /// tenant/sale-scoped).
    /// </summary>
    private static IReadOnlyList<(SaleItem SaleItem, decimal Quantity)> ValidateAndResolveLines(
        IReadOnlyCollection<SaleItem> saleItems, AvailabilityMap availability, FulfillmentMethod method,
        IReadOnlyList<FulfillmentItemInput> items)
    {
        var pairs = new List<(SaleItem, decimal)>(items.Count);
        foreach (var line in items)
        {
            var saleItem = saleItems.SingleOrDefault(i => i.Id == line.SaleItemId)
                ?? throw new BusinessRuleException(ErrorCodes.InvalidSaleItem, "A schedule line refers to an item that is not on this sale.");

            var available = availability.Available(saleItem.Id, method);
            if (line.Quantity > available)
            {
                throw new BusinessRuleException(
                    method == FulfillmentMethod.Pickup
                        ? ErrorCodes.PickupQuantityExceedsAvailable
                        : ErrorCodes.DeliveryQuantityExceedsAvailable,
                    $"Only {available} of \"{saleItem.ProductNameSnapshot}\" is still available to schedule for {MethodNoun(method)}.");
            }

            pairs.Add((saleItem, line.Quantity));
        }

        return pairs;
    }

    private async Task<Sale> LoadFulfillableSaleAsync(Guid tenantId, Guid saleId, CancellationToken ct)
    {
        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        if (sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded or SaleStatus.Refunded))
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotAllowed, "A fulfillment schedule can only be created for a completed sale.");
        }

        return sale;
    }

    /// <summary>
    /// Serializes concurrent allocation attempts against the same Sale's items. RowVersion (this
    /// codebase's default optimistic-concurrency idiom) only protects an UPDATE against a row that
    /// already exists — it cannot catch two concurrent create calls each INSERTing a brand-new
    /// DeliveryReceiptItem, since neither call updates a shared row. Without this lock, two transactions
    /// under SQL Server's default READ COMMITTED isolation could both read the same "4 available"
    /// snapshot before either commits, and both insert — over-allocating past what was ever marked for
    /// that method. <c>WITH (UPDLOCK, HOLDLOCK)</c> is the one pessimistic-lock precedent already in this
    /// codebase (<c>VoidSaleService</c>, for a different cross-aggregate race) — reused here for the same
    /// reason: it is the right tool exactly when RowVersion structurally cannot apply. The lock is held
    /// until the transaction commits or rolls back, so a second concurrent call blocks here until the
    /// first finishes, then re-reads an availability snapshot that correctly reflects what the first call
    /// just consumed.
    /// <para>Cancellation-with-conversion takes the SAME lock, which is what stops a conversion from
    /// racing an allocation: the intent columns cannot move under a create that has already snapshotted
    /// them, and a create cannot claim quantity a conversion is in the middle of moving.</para>
    /// </summary>
    /// <returns>The locked rows themselves, so callers get a post-lock intent snapshot without a second
    /// round trip.</returns>
    private async Task<IReadOnlyCollection<SaleItem>> LockSaleItemsAsync(Guid tenantId, Guid saleId, CancellationToken ct) =>
        await _db.SaleItems
            .FromSqlInterpolated($"SELECT * FROM SaleItems WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = {tenantId} AND SaleId = {saleId}")
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>Per-sale, PER-METHOD numbering — "Delivery 1" and "Pickup 1" coexist on the same sale.</summary>
    private async Task<int> NextSequenceNumberAsync(Guid tenantId, Guid saleId, FulfillmentMethod method, CancellationToken ct)
    {
        var max = await _db.DeliveryReceipts
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId && d.Method == method)
            .Select(d => (int?)d.SequenceNumber)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private DateOnly BusinessToday() =>
        DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime + ReportPeriodResolver.BusinessOffset);

    private void EnsureNotPastBusinessToday(DateOnly scheduledDate, FulfillmentMethod method)
    {
        if (scheduledDate < BusinessToday())
        {
            throw new BusinessRuleException(
                method == FulfillmentMethod.Pickup ? ErrorCodes.PickupScheduleDateInPast : ErrorCodes.DeliveryScheduleDateInPast,
                $"The scheduled {MethodNoun(method)} date cannot be in the past.");
        }
    }

    private static void EnsureMethod(DeliveryReceipt dr, FulfillmentMethod expectedMethod)
    {
        if (dr.Method != expectedMethod)
        {
            throw new BusinessRuleException(
                ErrorCodes.FulfillmentMethodMismatch,
                $"This schedule is a {MethodNoun(dr.Method)}, not a {MethodNoun(expectedMethod)}.");
        }
    }

    private static string MethodNoun(FulfillmentMethod method) => method switch
    {
        FulfillmentMethod.Pickup => "pickup",
        FulfillmentMethod.Delivery => "delivery",
        _ => "take-now",
    };

    private async Task<string> ResolvePreparedByNameAsync(CancellationToken ct) =>
        await _db.Users.Where(u => u.Id == _currentUser.UserId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

    /// <summary>The sale's conversion history, newest first, joined to Users for the actor name and to the
    /// sale's own lines for the product/variant snapshot.</summary>
    private async Task<IReadOnlyList<FulfillmentConversionDto>> LoadConversionsAsync(Guid tenantId, Sale sale, CancellationToken ct)
    {
        var rows = await _db.FulfillmentConversions.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.SaleId == sale.Id)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return Array.Empty<FulfillmentConversionDto>();
        }

        var actorIds = rows.Select(c => c.CreatedByUserId).Distinct().ToList();
        var actorNames = await _db.Users.AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, ct);

        var lines = sale.Items.ToDictionary(i => i.Id);

        var dtos = new List<FulfillmentConversionDto>(rows.Count);
        foreach (var c in rows)
        {
            lines.TryGetValue(c.SaleItemId, out var line);
            dtos.Add(new FulfillmentConversionDto(
                c.Id, c.SaleItemId,
                line?.ProductNameSnapshot ?? string.Empty,
                line?.VariantNameSnapshot,
                c.Quantity, c.FromMethod, c.ToMethod, c.SourceRecordId, c.ReplacementRecordId,
                c.Reason, c.CreatedAtUtc, actorNames.GetValueOrDefault(c.CreatedByUserId, string.Empty)));
        }

        return dtos;
    }

    /// <summary>Shared DTO builder for every read path (create's response, Get, List, the sale summary,
    /// and the complete/cancel responses). Only <see cref="DeliveryReceipt.Items"/> needs to already be
    /// loaded on <paramref name="dr"/>; everything else (branch/business/settings, live DeliveryCharge,
    /// the completed/cancelled actor names) is resolved here.</summary>
    private async Task<FulfillmentScheduleDto> MapToDtoAsync(DeliveryReceipt dr, CancellationToken ct)
    {
        var settings = await _settingsResolver.ResolveAsync(dr.BranchId, ct);

        var profile = await _db.TenantProfiles.Where(p => p.Id == dr.TenantId)
            .Select(p => new { p.Name, p.ContactNumber, p.TaxId })
            .SingleAsync(ct);
        var branch = await _db.Branches.Where(b => b.Id == dr.BranchId)
            .Select(b => new { b.Name, b.AddressLine1, b.City, b.Province, b.ContactNumber })
            .FirstOrDefaultAsync(ct);

        // A pickup never carries a delivery charge. The charge itself still lives on the Sale, untouched —
        // this is purely what THIS DTO reports.
        var deliveryCharge = dr.Method == FulfillmentMethod.Pickup
            ? 0m
            : dr.SaleId is { } saleId
                ? await _db.Sales.AsNoTracking().Where(s => s.Id == saleId).Select(s => s.DeliveryCharge).FirstOrDefaultAsync(ct)
                : 0m;

        var actorIds = new[] { dr.CompletedByUserId, dr.CancelledByUserId }.Where(id => id is not null).Select(id => id!.Value).ToList();
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
                return new FulfillmentItemDto(i.SaleItemId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, unitPrice, amount);
            })
            .ToList();

        return new FulfillmentScheduleDto(
            dr.Id, dr.SaleId, dr.RelatedSaleNumber, dr.Method, dr.SequenceNumber, dr.ScheduledDate, dr.Status, dr.CreatedAtUtc,
            branch?.Name ?? string.Empty, dr.RecipientName, dr.DeliveryAddress, dr.ContactNumber, dr.Notes,
            dr.PreparedByNameSnapshot,
            dr.CompletedAtUtc, dr.CompletedByUserId is { } cbu ? actorNames.GetValueOrDefault(cbu) : null,
            dr.CancelledAtUtc, dr.CancelledByUserId is { } xbu ? actorNames.GetValueOrDefault(xbu) : null,
            dr.CancellationReason, dr.CancellationDisposition,
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
