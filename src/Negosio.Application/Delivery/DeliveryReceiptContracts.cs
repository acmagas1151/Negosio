namespace Negosio.Application.Delivery;

public sealed record CreateDeliveryReceiptItemInput(
    Guid SaleItemId,
    decimal Quantity);

public sealed record CreateDeliveryReceiptRequest(
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    IReadOnlyList<CreateDeliveryReceiptItemInput>? Items);

public sealed record DeliveryReceiptItemDto(
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Amount);

public sealed record DeliveryReceiptDto(
    Guid Id,
    DateTime CreatedAtUtc,
    string? RelatedSaleNumber,
    string BranchName,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    string PreparedByName,
    IReadOnlyList<DeliveryReceiptItemDto> Items,
    /// <summary>Read live from the linked Sale (never a stored copy on this entity) — see the plan's
    /// Global Constraints. 0 for a delivery receipt with no linked sale (there is no charge to show).</summary>
    decimal DeliveryCharge,
    string? HeaderText,
    string? FooterText,
    string BusinessName,
    string? BusinessAddress,
    string? BusinessContactNumber,
    string? TaxId,
    bool ShowPrices,
    bool ShowRelatedSaleNumber,
    bool ShowContactNumber,
    bool ShowSignatureFields);

public interface IDeliveryReceiptService
{
    Task<DeliveryReceiptDto> CreateOrGetForSaleAsync(Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default);

    Task<DeliveryReceiptDto?> GetForSaleAsync(Guid saleId, CancellationToken ct = default);

    Task<DeliveryReceiptDto> GetAsync(Guid id, CancellationToken ct = default);
}
