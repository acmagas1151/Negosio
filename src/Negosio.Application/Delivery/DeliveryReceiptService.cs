using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Settings;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

/// <summary>
/// Creates and reprints the persistent one-per-sale <see cref="DeliveryReceipt"/>. Shape mirrors
/// <see cref="Sales.ReceiptService"/>: tenant guard, branch-scoped 404, settings-driven presentation,
/// business fields projected from <c>TenantProfile</c> + <c>Branch</c>. No document number, no
/// explicit transaction (a single <c>SaveChanges</c>).
/// </summary>
public sealed class DeliveryReceiptService : IDeliveryReceiptService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateDeliveryReceiptRequest> _validator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IReceiptSettingsResolver _settingsResolver;

    public DeliveryReceiptService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateDeliveryReceiptRequest> validator,
        IBranchAccessResolver branchAccess,
        IReceiptSettingsResolver settingsResolver)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
        _branchAccess = branchAccess;
        _settingsResolver = settingsResolver;
    }

    public async Task<DeliveryReceiptDto> CreateOrGetForSaleAsync(
        Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();

        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        // One-per-sale: an existing document is returned as-is (HTTP 200, no allocation). With
        // concurrent POSTs both could pass this check and insert — acceptable for v1 (no unique
        // constraint; the UI serialises via the button state).
        var existing = await _db.DeliveryReceipts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SaleId == saleId, ct);
        if (existing is not null)
        {
            return await GetAsync(existing.Id, ct);
        }

        // Server-authoritative status gate (mirrors ReturnService's allow-list). The UI hides the
        // button for a Voided sale, but that is UX only — a permanent, un-deletable DR must never be
        // created for a sale that is not in a delivered state. Placed after the existing-DR
        // short-circuit so a DR created while the sale was valid stays retrievable if it is later voided.
        if (sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded or SaleStatus.Refunded))
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotAllowed,
                "A delivery receipt can only be created for a completed sale.");
        }

        await _validator.ValidateAndThrowAppAsync(request, ct);

        var lines = ResolveItems(sale, request);
        if (lines.Count == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.InvalidSaleItem, "A delivery receipt must include at least one item.");
        }

        var preparedByName = await _db.Users.Where(u => u.Id == _currentUser.UserId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var dr = DeliveryReceipt.Create(
            tenantId,
            sale.BranchId,
            sale.Id,
            sale.SaleNumber,
            sequenceNumber: 1,
            scheduledDeliveryDate: DateOnly.FromDateTime(DateTime.UtcNow),
            request.RecipientName,
            request.DeliveryAddress,
            request.ContactNumber,
            request.DeliveryNotes,
            _currentUser.UserId,
            preparedByName);

        foreach (var (saleItem, quantity) in lines)
        {
            dr.AddItem(saleItem.Id, saleItem.ProductNameSnapshot, saleItem.VariantNameSnapshot, quantity, saleItem.UnitPrice);
        }

        _db.DeliveryReceipts.Add(dr);
        await _db.SaveChangesAsync(ct);

        return await GetAsync(dr.Id, ct);
    }

    public async Task<DeliveryReceiptDto?> GetForSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();

        var dr = await _db.DeliveryReceipts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SaleId == saleId, ct);
        if (dr is null)
        {
            return null;
        }

        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.", ct);
        return await GetAsync(dr.Id, ct);
    }

    public async Task<DeliveryReceiptDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();

        var dr = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.");

        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.", ct);

        var settings = await _settingsResolver.ResolveAsync(dr.BranchId, ct);

        var profile = await _db.TenantProfiles.Where(p => p.Id == tenantId)
            .Select(p => new { p.Name, p.ContactNumber, p.TaxId })
            .SingleAsync(ct);
        var branch = await _db.Branches.Where(b => b.Id == dr.BranchId)
            .Select(b => new { b.Name, b.AddressLine1, b.City, b.Province, b.ContactNumber })
            .FirstOrDefaultAsync(ct);

        var deliveryCharge = dr.SaleId is { } saleId
            ? await _db.Sales.AsNoTracking().Where(s => s.Id == saleId).Select(s => s.DeliveryCharge).FirstOrDefaultAsync(ct)
            : 0m;

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
                return new DeliveryReceiptItemDto(
                    i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, unitPrice, amount);
            })
            .ToList();

        return new DeliveryReceiptDto(
            dr.Id,
            dr.CreatedAtUtc,
            dr.RelatedSaleNumber,
            branch?.Name ?? string.Empty,
            dr.RecipientName,
            dr.DeliveryAddress,
            dr.ContactNumber,
            dr.DeliveryNotes,
            dr.PreparedByNameSnapshot,
            items,
            deliveryCharge,
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

    /// <summary>
    /// Maps <paramref name="request"/>.Items to <c>(SaleItem, qty)</c> pairs — each line must refer to
    /// an item on the sale, be listed at most once, and may not exceed its sold quantity. When
    /// <c>Items</c> is null, every sale item is delivered at full quantity; a non-null but empty (or
    /// otherwise line-less) selection is rejected — a Delivery Receipt is permanent and must not be
    /// created without lines.
    /// </summary>
    private static IReadOnlyList<(SaleItem SaleItem, decimal Quantity)> ResolveItems(
        Sale sale, CreateDeliveryReceiptRequest request)
    {
        if (request.Items is null)
        {
            return sale.Items
                .OrderBy(i => i.CreatedAtUtc)
                .ThenBy(i => i.Id)
                .Select(i => (i, i.Quantity))
                .ToList();
        }

        if (request.Items.Count == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.InvalidSaleItem, "A delivery receipt must include at least one item.");
        }

        if (request.Items.GroupBy(i => i.SaleItemId).Any(g => g.Count() > 1))
        {
            throw new BusinessRuleException(
                ErrorCodes.InvalidSaleItem, "Each sale item may be listed at most once.");
        }

        var pairs = new List<(SaleItem, decimal)>();
        foreach (var line in request.Items)
        {
            var saleItem = sale.Items.SingleOrDefault(i => i.Id == line.SaleItemId)
                ?? throw new BusinessRuleException(
                    ErrorCodes.InvalidSaleItem, "A delivery line refers to an item that is not on this sale.");

            if (line.Quantity <= 0m)
            {
                throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Delivery quantity must be greater than zero.");
            }

            if (line.Quantity > saleItem.Quantity)
            {
                throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Cannot deliver more than was sold.");
            }

            pairs.Add((saleItem, line.Quantity));
        }

        return pairs;
    }

    /// <summary>
    /// Branch-scoped users may only see their own branch's documents (404, not 403). Owner/Admin are
    /// unrestricted.
    /// </summary>
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
