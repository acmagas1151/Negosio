using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Settings;

/// <summary>
/// Reads and writes receipt presentation settings. There are two scopes: the tenant-default row
/// (<c>BranchId == null</c>, Owner/Admin only) and per-branch override rows. A branch with no
/// override row inherits the tenant default; a tenant with no row at all inherits
/// <see cref="ReceiptSettingsValues.HardcodedDefault"/>.
/// </summary>
public sealed class ReceiptSettingsService : IReceiptSettingsService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IValidator<UpdateReceiptSettingsRequest> _validator;

    public ReceiptSettingsService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IBranchAccessResolver branchAccess,
        IValidator<UpdateReceiptSettingsRequest> validator)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
        _validator = validator;
    }

    public async Task<ReceiptSettingsDto> GetAsync(Guid? branchId, CancellationToken cancellationToken = default)
    {
        RequireTenant();

        var (row, effective, scope, isOverride) = await LoadEffectiveAsync(branchId, cancellationToken);

        var canEdit = _branchAccess.IsAllBranch
            || (branchId is { } b && b == await _branchAccess.AssignedBranchIdAsync(cancellationToken));

        string? updatedByName = null;
        if (row is not null)
        {
            updatedByName = await _db.Users.AsNoTracking()
                .Where(u => u.Id == row.UpdatedByUserId)
                .Select(u => u.FirstName + " " + u.LastName)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return Project(scope, branchId, canEdit, isOverride, effective, row?.UpdatedAtUtc, updatedByName);
    }

    public async Task<ReceiptSettingsDto> UpdateAsync(
        Guid? branchId, UpdateReceiptSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        var userId = _currentUser.UserId;
        var values = Normalize(request);

        if (branchId is { } bid)
        {
            // Throws BRANCH_FORBIDDEN for a branch-scoped user targeting another branch; also
            // validates the branch exists and is active.
            await _branchAccess.ResolveTargetBranchAsync(bid, cancellationToken: cancellationToken);

            var (row, effective, _, _) = await LoadEffectiveAsync(bid, cancellationToken);
            if (row is null)
            {
                // Seed the new override from the values the branch currently shows (tenant default
                // or hardcoded default), then apply the request on top.
                var created = ReceiptSettings.CreateFrom(tenantId, bid, effective, userId);
                created.Update(values, userId);
                _db.ReceiptSettings.Add(created);
            }
            else
            {
                row.Update(values, userId);
            }

            await _db.SaveChangesAsync(cancellationToken);
            return await GetAsync(bid, cancellationToken);
        }

        if (!_branchAccess.IsAllBranch)
        {
            throw new ForbiddenAppException(
                ErrorCodes.BranchForbidden, "Managers can't edit the tenant-default receipt settings.");
        }

        var (tenantRow, _, _, _) = await LoadEffectiveAsync(null, cancellationToken);
        if (tenantRow is null)
        {
            var created = ReceiptSettings.CreateDefault(tenantId, null, userId);
            created.Update(values, userId);
            _db.ReceiptSettings.Add(created);
        }
        else
        {
            tenantRow.Update(values, userId);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(null, cancellationToken);
    }

    public async Task ResetAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _branchAccess.ResolveTargetBranchAsync(branchId, cancellationToken: cancellationToken);

        var row = await _db.ReceiptSettings
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.BranchId == branchId, cancellationToken);
        if (row is null)
        {
            return;
        }

        _db.ReceiptSettings.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the values a scope currently shows plus the backing row (if any). See the class
    /// summary for the inheritance chain. Task 7 replaces this with the shared resolver.
    /// </summary>
    private async Task<(ReceiptSettings? Row, ReceiptSettingsValues Effective, ReceiptSettingsScope Scope, bool IsOverride)>
        LoadEffectiveAsync(Guid? branchId, CancellationToken cancellationToken)
    {
        var tenantId = _currentUser.TenantId;

        if (branchId is { } bid)
        {
            var branchRow = await _db.ReceiptSettings
                .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.BranchId == bid, cancellationToken);
            if (branchRow is not null)
            {
                return (branchRow, branchRow.ToValues(), ReceiptSettingsScope.Branch, true);
            }
        }

        var tenantRow = await _db.ReceiptSettings
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.BranchId == null, cancellationToken);

        if (tenantRow is not null)
        {
            return branchId is null
                ? (tenantRow, tenantRow.ToValues(), ReceiptSettingsScope.TenantDefault, false)
                : (null, tenantRow.ToValues(), ReceiptSettingsScope.Branch, false);
        }

        return (
            null,
            ReceiptSettingsValues.HardcodedDefault,
            branchId is null ? ReceiptSettingsScope.TenantDefault : ReceiptSettingsScope.Branch,
            false);
    }

    private static ReceiptSettingsValues Normalize(UpdateReceiptSettingsRequest r) => new(
        r.Width,
        ReceiptText.Normalize(r.SalesHeaderText),
        ReceiptText.Normalize(r.SalesFooterText),
        r.SalesShowBranch,
        r.SalesShowCashier,
        r.SalesShowPaymentMethod,
        r.SalesShowTaxLine,
        r.SalesShowReferenceNumber,
        ReceiptText.Normalize(r.DeliveryHeaderText),
        ReceiptText.Normalize(r.DeliveryFooterText),
        r.DeliveryShowPrices,
        r.DeliveryShowRelatedSaleNumber,
        r.DeliveryShowContactNumber,
        r.DeliveryShowSignatureFields);

    private static ReceiptSettingsDto Project(
        ReceiptSettingsScope scope,
        Guid? branchId,
        bool canEdit,
        bool isOverride,
        ReceiptSettingsValues v,
        DateTime? updatedAtUtc,
        string? updatedByName) => new(
        scope,
        branchId,
        canEdit,
        isOverride,
        v.Width,
        v.SalesHeaderText,
        v.SalesFooterText,
        v.SalesShowBranch,
        v.SalesShowCashier,
        v.SalesShowPaymentMethod,
        v.SalesShowTaxLine,
        v.SalesShowReferenceNumber,
        v.DeliveryHeaderText,
        v.DeliveryFooterText,
        v.DeliveryShowPrices,
        v.DeliveryShowRelatedSaleNumber,
        v.DeliveryShowContactNumber,
        v.DeliveryShowSignatureFields,
        updatedAtUtc,
        updatedByName);

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
