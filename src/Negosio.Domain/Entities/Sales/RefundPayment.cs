using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>Money paid back to the customer for a <see cref="SaleReturn"/>. Recorded, not gateway-processed.</summary>
public class RefundPayment : Entity
{
    private RefundPayment()
    {
    }

    internal RefundPayment(Guid tenantId, Guid saleReturnId, PaymentMethod method, decimal amount, string? referenceNumber)
    {
        TenantId = tenantId;
        SaleReturnId = saleReturnId;
        Method = method;
        Amount = amount;
        ReferenceNumber = string.IsNullOrWhiteSpace(referenceNumber) ? null : referenceNumber.Trim();
    }

    public Guid TenantId { get; private set; }

    public Guid SaleReturnId { get; private set; }

    public PaymentMethod Method { get; private set; }

    public decimal Amount { get; private set; }

    public string? ReferenceNumber { get; private set; }
}
