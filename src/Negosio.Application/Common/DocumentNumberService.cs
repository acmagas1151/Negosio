using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Domain.Enums;

namespace Negosio.Application.Common;

public interface IDocumentNumberService
{
    /// <summary>
    /// Atomically allocates the next number for (tenant, branch, type) and formats it.
    /// Sales and returns share one <b>branch transaction sequence</b> (both pass
    /// <see cref="DocumentNumberType.Sale"/>) formatted as a bare zero-padded 8-digit string,
    /// e.g. <c>00000001</c>; reserved future types keep a prefixed format. MUST be called inside
    /// the caller's database transaction so the allocation commits or rolls back with the document.
    /// </summary>
    Task<string> NextAsync(
        Guid tenantId,
        Guid branchId,
        DocumentNumberType type,
        string branchCode,
        CancellationToken cancellationToken = default);
}

public sealed class DocumentNumberService : IDocumentNumberService
{
    private readonly ITenantDbContext _db;

    public DocumentNumberService(ITenantDbContext db)
    {
        _db = db;
    }

    public async Task<string> NextAsync(
        Guid tenantId,
        Guid branchId,
        DocumentNumberType type,
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        var value = await AllocateAsync(tenantId, branchId, type, cancellationToken);

        // Sales and returns share the branch transaction sequence: a bare 8-digit running number.
        // Reserved future document types keep a prefixed, branch-scoped format.
        return type switch
        {
            DocumentNumberType.Sale or DocumentNumberType.Return => $"{value:D8}",
            DocumentNumberType.PurchaseOrder => $"PO-{branchCode}-{value:D6}",
            DocumentNumberType.StockTransfer => $"TRN-{branchCode}-{value:D6}",
            _ => $"DOC-{branchCode}-{value:D6}"
        };
    }

    private async Task<long> AllocateAsync(Guid tenantId, Guid branchId, DocumentNumberType type, CancellationToken cancellationToken)
    {
        var typeValue = (int)type;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // Atomic: SQL Server locks the row for the increment and returns the new value.
            // UPDATE...OUTPUT is non-composable, so materialise with ToListAsync (no TOP wrapper).
            var allocated = (await _db.Database
                .SqlQuery<long>($@"
                    UPDATE DocumentNumberCounters
                    SET LastNumber = LastNumber + 1
                    OUTPUT INSERTED.LastNumber AS Value
                    WHERE TenantId = {tenantId} AND BranchId = {branchId} AND [Type] = {typeValue}")
                .ToListAsync(cancellationToken))
                .FirstOrDefault();

            if (allocated > 0)
            {
                return allocated;
            }

            // No counter row yet for this (tenant, branch, type) — create it and loop back to UPDATE.
            try
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO DocumentNumberCounters (Id, TenantId, BranchId, [Type], LastNumber)
                       VALUES ({Guid.NewGuid()}, {tenantId}, {branchId}, {typeValue}, 0)",
                    cancellationToken);
            }
            catch (Exception ex) when (SqlUniqueViolation.Is(ex))
            {
                // Another transaction created the row first — fine, retry the UPDATE.
            }
        }

        throw new InvalidOperationException("Could not allocate a document number after multiple attempts.");
    }
}
