using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Domain.Common;

namespace Negosio.Application.Settings;

/// <inheritdoc />
public sealed class ReceiptSettingsResolver : IReceiptSettingsResolver
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly Dictionary<Guid, ReceiptSettingsValues> _memo = new();

    public ReceiptSettingsResolver(ITenantDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ReceiptSettingsValues> ResolveAsync(Guid branchId, CancellationToken ct = default)
    {
        if (_memo.TryGetValue(branchId, out var cached))
        {
            return cached;
        }

        var tenantId = _currentUser.TenantId;

        var branchRow = await _db.ReceiptSettings.AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.BranchId == branchId, ct);

        var values = branchRow?.ToValues();

        if (values is null)
        {
            var tenantRow = await _db.ReceiptSettings.AsNoTracking()
                .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.BranchId == null, ct);
            values = tenantRow?.ToValues();
        }

        values ??= ReceiptSettingsValues.HardcodedDefault;

        _memo[branchId] = values;
        return values;
    }
}
