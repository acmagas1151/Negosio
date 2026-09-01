using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;

namespace Negosio.Api.Middleware;

/// <summary>
/// After tenant routing: a branch-scoped user may continue only while their assigned branch is
/// active. This blocks a still-valid JWT the moment the branch is deactivated (401 BRANCH_INACTIVE),
/// without a token blacklist — one indexed tenant-DB lookup per branch-scoped request. Owner/Admin
/// and anonymous requests pass straight through (and never touch the tenant DB). The thrown
/// <see cref="BranchInactiveException"/> is rendered by <see cref="ExceptionHandlingMiddleware"/>.
/// </summary>
public sealed class BranchAccessMiddleware
{
    private readonly RequestDelegate _next;

    public BranchAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser)
    {
        if (!currentUser.IsAuthenticated || BranchRoles.IsAllBranch(currentUser.Role))
        {
            await _next(context);
            return;
        }

        var db = context.RequestServices.GetRequiredService<ITenantDbContext>();
        var branchActive = await db.Users.AsNoTracking()
            .Where(u => u.Id == currentUser.UserId && u.BranchId != null)
            .AnyAsync(u => db.Branches.Any(b => b.Id == u.BranchId && b.IsActive), context.RequestAborted);

        if (!branchActive)
        {
            throw new BranchInactiveException();
        }

        await _next(context);
    }
}
