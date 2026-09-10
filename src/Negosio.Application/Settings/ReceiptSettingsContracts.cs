using Negosio.Domain.Enums;

namespace Negosio.Application.Settings;

public sealed record UpdateReceiptSettingsRequest(
    ReceiptWidth Width,
    string? SalesHeaderText,
    string? SalesFooterText,
    bool SalesShowBranch,
    bool SalesShowCashier,
    bool SalesShowPaymentMethod,
    bool SalesShowTaxLine,
    bool SalesShowReferenceNumber,
    string? DeliveryHeaderText,
    string? DeliveryFooterText,
    bool DeliveryShowPrices,
    bool DeliveryShowRelatedSaleNumber,
    bool DeliveryShowContactNumber,
    bool DeliveryShowSignatureFields);

/// <summary>Which layer a <see cref="ReceiptSettingsDto"/> describes.</summary>
public enum ReceiptSettingsScope
{
    TenantDefault,
    Branch,
}

/// <summary>
/// Effective receipt settings for a scope. <see cref="IsOverride"/> is true when a row actually
/// exists at this scope (a branch override); false means the values are inherited (tenant default
/// shown for a branch, or the hardcoded default shown for the tenant).
/// </summary>
public sealed record ReceiptSettingsDto(
    ReceiptSettingsScope Scope,
    Guid? BranchId,
    bool CanEdit,
    bool IsOverride,
    ReceiptWidth Width,
    string? SalesHeaderText,
    string? SalesFooterText,
    bool SalesShowBranch,
    bool SalesShowCashier,
    bool SalesShowPaymentMethod,
    bool SalesShowTaxLine,
    bool SalesShowReferenceNumber,
    string? DeliveryHeaderText,
    string? DeliveryFooterText,
    bool DeliveryShowPrices,
    bool DeliveryShowRelatedSaleNumber,
    bool DeliveryShowContactNumber,
    bool DeliveryShowSignatureFields,
    DateTime? UpdatedAtUtc,
    string? UpdatedByName);

public interface IReceiptSettingsService
{
    Task<ReceiptSettingsDto> GetAsync(Guid? branchId, CancellationToken cancellationToken = default);

    Task<ReceiptSettingsDto> UpdateAsync(Guid? branchId, UpdateReceiptSettingsRequest request, CancellationToken cancellationToken = default);

    Task ResetAsync(Guid branchId, CancellationToken cancellationToken = default);
}
