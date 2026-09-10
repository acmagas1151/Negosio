using Negosio.Domain.Enums;

namespace Negosio.Domain.Common;

/// <summary>
/// A complete, self-contained snapshot of receipt presentation config — the shape shared by the
/// <c>ReceiptSettings</c> entity, the resolver, and the API DTO. There is no field-level merging:
/// a settings row is always one whole <see cref="ReceiptSettingsValues"/>.
/// </summary>
public sealed record ReceiptSettingsValues(
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
    bool DeliveryShowSignatureFields)
{
    /// <summary>Reproduces the receipt output that existed before Receipt Settings — used when a
    /// tenant has configured nothing. No custom header/footer (fall back to the business-info block
    /// and "Thank you!"), every option on, 80mm.</summary>
    public static ReceiptSettingsValues HardcodedDefault { get; } = new(
        Width: ReceiptWidth.Mm80,
        SalesHeaderText: null,
        SalesFooterText: null,
        SalesShowBranch: true,
        SalesShowCashier: true,
        SalesShowPaymentMethod: true,
        SalesShowTaxLine: true,
        SalesShowReferenceNumber: true,
        DeliveryHeaderText: null,
        DeliveryFooterText: null,
        DeliveryShowPrices: true,
        DeliveryShowRelatedSaleNumber: true,
        DeliveryShowContactNumber: true,
        DeliveryShowSignatureFields: true);
}
