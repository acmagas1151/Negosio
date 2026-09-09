using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

public class ReceiptSettings : Entity
{
    private ReceiptSettings() { }

    private ReceiptSettings(Guid tenantId, Guid? branchId, ReceiptSettingsValues v, Guid userId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        Apply(v);
        UpdatedByUserId = userId;
    }

    public Guid TenantId { get; private set; }
    public Guid? BranchId { get; private set; }

    public ReceiptWidth Width { get; private set; }
    public string? SalesHeaderText { get; private set; }
    public string? SalesFooterText { get; private set; }
    public bool SalesShowBranch { get; private set; }
    public bool SalesShowCashier { get; private set; }
    public bool SalesShowPaymentMethod { get; private set; }
    public bool SalesShowTaxLine { get; private set; }
    public bool SalesShowReferenceNumber { get; private set; }
    public string? DeliveryHeaderText { get; private set; }
    public string? DeliveryFooterText { get; private set; }
    public bool DeliveryShowPrices { get; private set; }
    public bool DeliveryShowRelatedSaleNumber { get; private set; }
    public bool DeliveryShowContactNumber { get; private set; }
    public bool DeliveryShowSignatureFields { get; private set; }

    public Guid UpdatedByUserId { get; private set; }

    public static ReceiptSettings CreateDefault(Guid tenantId, Guid? branchId, Guid userId)
        => new(tenantId, branchId, ReceiptSettingsValues.HardcodedDefault, userId);

    public static ReceiptSettings CreateFrom(Guid tenantId, Guid? branchId, ReceiptSettingsValues seed, Guid userId)
        => new(tenantId, branchId, seed, userId);

    public void Update(ReceiptSettingsValues values, Guid userId)
    {
        Apply(values);
        UpdatedByUserId = userId;
        Touch();
    }

    public ReceiptSettingsValues ToValues() => new(
        Width, SalesHeaderText, SalesFooterText, SalesShowBranch, SalesShowCashier,
        SalesShowPaymentMethod, SalesShowTaxLine, SalesShowReferenceNumber,
        DeliveryHeaderText, DeliveryFooterText, DeliveryShowPrices, DeliveryShowRelatedSaleNumber,
        DeliveryShowContactNumber, DeliveryShowSignatureFields);

    private void Apply(ReceiptSettingsValues v)
    {
        Width = v.Width;
        SalesHeaderText = v.SalesHeaderText;
        SalesFooterText = v.SalesFooterText;
        SalesShowBranch = v.SalesShowBranch;
        SalesShowCashier = v.SalesShowCashier;
        SalesShowPaymentMethod = v.SalesShowPaymentMethod;
        SalesShowTaxLine = v.SalesShowTaxLine;
        SalesShowReferenceNumber = v.SalesShowReferenceNumber;
        DeliveryHeaderText = v.DeliveryHeaderText;
        DeliveryFooterText = v.DeliveryFooterText;
        DeliveryShowPrices = v.DeliveryShowPrices;
        DeliveryShowRelatedSaleNumber = v.DeliveryShowRelatedSaleNumber;
        DeliveryShowContactNumber = v.DeliveryShowContactNumber;
        DeliveryShowSignatureFields = v.DeliveryShowSignatureFields;
    }
}
