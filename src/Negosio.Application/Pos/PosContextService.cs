using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Pos;

/// <summary>The branch a POS session runs against, and whether the operator gets to choose it.</summary>
public sealed record PosContextDto(
    Guid? BranchId,
    string? BranchName,
    bool CanPickBranch,
    IReadOnlyList<PosBranchDto> Branches);

public sealed record PosBranchDto(Guid Id, string Name, string Code);

/// <summary>A register in the resolved branch plus its open-session state (for the POS picker).</summary>
public sealed record PosRegisterDto(
    Guid Id,
    string Name,
    string Code,
    bool IsActive,
    PosOpenSessionDto? OpenSession);

public sealed record PosOpenSessionDto(Guid SessionId, Guid OpenedByUserId, string OpenedByName, bool Mine);

public interface IPosContextService
{
    Task<PosContextDto> GetContextAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PosRegisterDto>> GetRegistersAsync(Guid? branchId, CancellationToken cancellationToken = default);
}

public sealed class PosContextService : IPosContextService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;

    public PosContextService(ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
    }

    public async Task<PosContextDto> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        var assigned = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } branchId)
        {
            var name = await _db.Branches.Where(b => b.Id == branchId).Select(b => b.Name).SingleAsync(cancellationToken);
            return new PosContextDto(branchId, name, CanPickBranch: false,
                new[] { new PosBranchDto(branchId, name, string.Empty) });
        }

        // Owner/Admin: pick among active branches (auto when there is exactly one).
        var active = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.IsActive)
            .OrderBy(b => b.Name)
            .Select(b => new PosBranchDto(b.Id, b.Name, b.Code))
            .ToListAsync(cancellationToken);

        var sole = active.Count == 1 ? active[0] : null;
        return new PosContextDto(
            sole?.Id, sole?.Name, CanPickBranch: active.Count > 1, active);
    }

    public async Task<IReadOnlyList<PosRegisterDto>> GetRegistersAsync(Guid? branchId, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;
        var resolved = await _branchAccess.ResolveTargetBranchAsync(branchId, cancellationToken: cancellationToken);

        var registers = await _db.Registers.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.BranchId == resolved && r.IsActive)
            .OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.Code, r.IsActive })
            .ToListAsync(cancellationToken);

        var registerIds = registers.Select(r => r.Id).ToList();
        var openSessions = await (
            from s in _db.RegisterSessions.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.Status == RegisterSessionStatus.Open && registerIds.Contains(s.RegisterId))
            join u in _db.Users on s.OpenedByUserId equals u.Id
            select new { s.RegisterId, s.Id, s.OpenedByUserId, Name = u.FirstName + " " + u.LastName })
            .ToListAsync(cancellationToken);

        var byRegister = openSessions.ToDictionary(x => x.RegisterId);

        return registers.Select(r =>
        {
            PosOpenSessionDto? open = null;
            if (byRegister.TryGetValue(r.Id, out var s))
            {
                open = new PosOpenSessionDto(s.Id, s.OpenedByUserId, s.Name, s.OpenedByUserId == _currentUser.UserId);
            }
            return new PosRegisterDto(r.Id, r.Name, r.Code, r.IsActive, open);
        }).ToList();
    }
}
