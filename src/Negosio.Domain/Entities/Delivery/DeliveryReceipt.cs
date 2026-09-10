using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// A printable delivery document generated from a completed <see cref="Sale"/>. Captures snapshot details
/// of what was delivered (items, prices, recipient address) at the time of delivery. Identified solely by
/// its <see cref="Entity.Id"/>.
/// </summary>
public class DeliveryReceipt : Entity
{
    private readonly List<DeliveryReceiptItem> _items = new();

    private DeliveryReceipt()
    {
        RecipientName = string.Empty;
        DeliveryAddress = string.Empty;
        PreparedByNameSnapshot = string.Empty;
    }

    private DeliveryReceipt(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? deliveryNotes,
        Guid preparedByUserId,
        string preparedByNameSnapshot)
    {
        TenantId = tenantId;
        BranchId = branchId;
        SaleId = saleId;
        RelatedSaleNumber = relatedSaleNumber;
        RecipientName = recipientName;
        DeliveryAddress = deliveryAddress;
        ContactNumber = contactNumber;
        DeliveryNotes = deliveryNotes;
        PreparedByUserId = preparedByUserId;
        PreparedByNameSnapshot = preparedByNameSnapshot;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? SaleId { get; private set; }

    public string? RelatedSaleNumber { get; private set; }

    public string RecipientName { get; private set; }

    public string DeliveryAddress { get; private set; }

    public string? ContactNumber { get; private set; }

    public string? DeliveryNotes { get; private set; }

    public Guid PreparedByUserId { get; private set; }

    public string PreparedByNameSnapshot { get; private set; }

    public IReadOnlyCollection<DeliveryReceiptItem> Items => _items.AsReadOnly();

    public static DeliveryReceipt Create(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? deliveryNotes,
        Guid preparedByUserId,
        string preparedByNameSnapshot)
    {
        var trimmedRecipientName = recipientName?.Trim() ?? string.Empty;
        var trimmedDeliveryAddress = deliveryAddress?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedRecipientName))
        {
            throw new ArgumentException("Recipient name is required.", nameof(recipientName));
        }

        if (string.IsNullOrWhiteSpace(trimmedDeliveryAddress))
        {
            throw new ArgumentException("Delivery address is required.", nameof(deliveryAddress));
        }

        var trimmedContactNumber = contactNumber?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedContactNumber))
        {
            trimmedContactNumber = null;
        }

        var trimmedDeliveryNotes = deliveryNotes?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDeliveryNotes))
        {
            trimmedDeliveryNotes = null;
        }

        var trimmedRelatedSaleNumber = relatedSaleNumber?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedRelatedSaleNumber))
        {
            trimmedRelatedSaleNumber = null;
        }

        return new DeliveryReceipt(
            tenantId,
            branchId,
            saleId,
            trimmedRelatedSaleNumber,
            trimmedRecipientName,
            trimmedDeliveryAddress,
            trimmedContactNumber,
            trimmedDeliveryNotes,
            preparedByUserId,
            preparedByNameSnapshot);
    }

    public DeliveryReceiptItem AddItem(
        string productNameSnapshot,
        string? variantNameSnapshot,
        decimal quantity,
        decimal? unitPrice)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        var item = new DeliveryReceiptItem(
            TenantId,
            Id,
            productNameSnapshot,
            variantNameSnapshot,
            quantity,
            unitPrice);
        _items.Add(item);
        return item;
    }
}
