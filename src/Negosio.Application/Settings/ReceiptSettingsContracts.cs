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
