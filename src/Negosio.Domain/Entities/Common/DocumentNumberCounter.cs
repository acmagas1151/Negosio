using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A monotonic counter that hands out human-readable document numbers for one
/// (tenant, branch, document type). Allocation is done with an atomic
/// <c>UPDATE ... OUTPUT INSERTED.LastNumber</c> inside the caller's transaction — never
/// <c>COUNT(*) + 1</c>. Committed numbers are never reused (a voided/refunded sale keeps its number).
/// Not an <see cref="Common.Entity"/>: it has no audit timestamps and is infrastructure, not an aggregate.
/// </summary>
public class DocumentNumberCounter
{
    private DocumentNumberCounter()
    {
    }

    public DocumentNumberCounter(Guid tenantId, Guid branchId, DocumentNumberType type, long lastNumber)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        BranchId = branchId;
        Type = type;
        LastNumber = lastNumber;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public DocumentNumberType Type { get; private set; }

    public long LastNumber { get; private set; }
}
