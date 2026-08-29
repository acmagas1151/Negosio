using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A payment recorded against a <see cref="Sale"/>. A sale may have several payments (split tender);
/// Phase 3 checkout typically sends one. Phase 3 records payment methods only — no gateway calls.
/// </summary>
public class Payment : Entity
{
    private Payment()
    {
    }

    internal Payment(
        Guid tenantId,
        Guid saleId,
        PaymentMethod method,
        decimal amount,
        string? referenceNumber,
        decimal? receivedAmount,
        decimal? changeAmount)
    {
        TenantId = tenantId;
        SaleId = saleId;
        Method = method;
        Amount = amount;
        ReferenceNumber = string.IsNullOrWhiteSpace(referenceNumber) ? null : referenceNumber.Trim();
        ReceivedAmount = receivedAmount;
        ChangeAmount = changeAmount;
    }

    public Guid TenantId { get; private set; }

    public Guid SaleId { get; private set; }

    public PaymentMethod Method { get; private set; }

    public decimal Amount { get; private set; }

    public string? ReferenceNumber { get; private set; }

    public decimal? ReceivedAmount { get; private set; }

    public decimal? ChangeAmount { get; private set; }
}
