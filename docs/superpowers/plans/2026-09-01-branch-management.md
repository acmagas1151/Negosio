# Phase 5 — Branch Management & Branch-Scoped Access — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps
> use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Branch Management v1 (CRUD + lifecycle) and lock every non-Owner/Admin user to
one assigned branch — enforced at authentication, per request, and inside every branch-aware
service.

**Architecture:** Modular monolith, database-per-tenant. `Branch` and `User.BranchId` live in
the tenant DB; `StaffInvitation.BranchId` in the platform DB. A new `BranchAccessResolver`
(Application) is the single point that turns "requested branchId" into "allowed branchId or
403". A new `BranchAccessMiddleware` (after `TenantResolutionMiddleware`) blocks a
branch-scoped user whose assigned branch is inactive. No new infra — no Redis, no queues.

**Tech Stack:** C# / .NET 9, ASP.NET Core, EF Core 9, SQL Server, FluentValidation, xUnit,
FluentAssertions, WebApplicationFactory · React 19, TS strict, Vite, React Router 7, TanStack
Query v5, Tailwind v4, oxlint.

**Spec:** `docs/superpowers/specs/2026-09-01-branch-management-design.md` — read it first; this
plan implements it. Section references below (§n) point into that spec.

## Global Constraints

- Work only on `feature/branch-management` (already created off `master` @ `61d2b01`).
  **Never merge to master.** Stop after Task 12.
- No `Co-Authored-By: Claude` / `Generated with Claude Code` commit trailer.
- Branch-scoped roles = `Manager, Cashier, InventoryStaff, KitchenStaff, Viewer`
  (`UserRole` values 3–7). All-branch = `Owner (1), Admin (2)`.
- Branch **code is immutable** after creation. Branch **name + address** are editable.
- Never delete a branch, a user, a session, inventory, or sales. Deactivate only.
- Tenant is always resolved from `ICurrentUser` / the JWT, never from a request body.
- Money/stock stay server-authoritative. Don't weaken lint / TS / validation / authz to pass.
- Kill stale `Negosio.Api` / `vite` processes before `dotnet build` / `dotnet test`.
- Run the API: `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api --launch-profile http`.
- EF migrations use the existing invocation style, e.g.:
  `dotnet dotnet-ef migrations add <Name> --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api --output-dir Persistence/Migrations/Tenant`
  (and `--context PlatformDbContext … --output-dir Persistence/Migrations/Platform`).
- Baseline tests: **76 unit / 114 integration**. Report actual finals in the report.

---

## File Structure

### Backend — new files

| File | Responsibility |
|---|---|
| `src/Negosio.Application/Branches/BranchRoles.cs` | `BranchScoped` set + `IsAllBranch(UserRole)` |
| `src/Negosio.Application/Branches/BranchAccessResolver.cs` | `IBranchAccessResolver` + impl (§4) |
| `src/Negosio.Application/Branches/BranchManagementService.cs` | `IBranchManagementService` + impl |
| `src/Negosio.Application/Branches/BranchValidators.cs` | Create/Update request validators |
| `src/Negosio.Api/Middleware/BranchAccessMiddleware.cs` | per-request inactive-branch gate (§5.2) |
| `src/Negosio.Application/Pos/PosContextService.cs` | `GET /api/pos/context` + `/api/pos/registers` read models (§10.3) |
| `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/*_AddUserBranchAndSessionOwnerIndex.cs` | migration (generated) |
| `src/Negosio.Infrastructure/Persistence/Migrations/Platform/*_AddStaffInvitationBranch.cs` | migration (generated) |

### Backend — modified files

`Branch.cs`, `User.cs`, `StaffInvitation.cs` (domain methods) · `BranchContracts.cs`,
`BranchQueryService.cs` · `BranchesController.cs` · `AuthorizationPolicies.cs` ·
`ErrorCodes.cs` · `AppException`-consumers only (no new type needed) ·
`AuthService.cs` (login branch check) · `ConfigureJwtBearerOptions.cs` (unchanged — noted) ·
`Program.cs` (register middleware + DI) · `DependencyInjection.cs` (Application DI) ·
`UserConfiguration.cs`, `StaffInvitationConfiguration.cs`, `RegisterSessionConfiguration.cs` ·
`InventoryService.cs`, `InventoryContracts.cs` · `RegisterService.cs`,
`RegisterSessionService.cs`, `RegisterContracts.cs` · `RegisterSessionsController.cs` (force-close) ·
`PosCatalogService.cs`, `CheckoutService.cs`, `CheckoutValidator.cs` ·
`SaleQueryService.cs`, `ReceiptService.cs`, `ReturnService.cs` ·
`DashboardService.cs` · `StaffContracts.cs`, `StaffService.cs`, `StaffValidators.cs`,
`StaffInvitationService.cs` · `AuthController.cs` (invitation preview branch — optional) ·
`IPlatformDbContext` / `ITenantDbContext` (no change — `Branches`/`Users` already exposed).

### Frontend — new files

`src/api/branches.ts` · `src/pages/BranchesPage.tsx` ·
`src/components/branches/BranchFormModal.tsx` · `src/components/branches/BranchStatusBadge.tsx` ·
`src/components/pos/BranchPicker.tsx` · `src/components/staff/ChangeBranchModal.tsx`

### Frontend — modified files

`src/api/types.ts` · `src/api/inventory.ts` (re-export) · `src/api/pos.ts` · `src/api/staff.ts` ·
`src/lib/useCan.ts` · `src/lib/nav.ts` · `src/lib/roles.ts` · `src/lib/posStorage.ts` ·
`src/App.tsx` · `src/pages/PosPage.tsx` · `src/pages/SalesPage.tsx` · `src/pages/SaleDetailPage.tsx` ·
`src/pages/StaffPage.tsx` · `src/components/pos/RegisterPicker.tsx` ·
`src/components/pos/PosShell.tsx` · `src/components/staff/InviteStaffModal.tsx` ·
`src/components/staff/ChangeRoleModal.tsx`

### Tests — new files

`tests/Negosio.UnitTests/Branches/BranchDomainTests.cs` ·
`tests/Negosio.UnitTests/Branches/BranchRolesTests.cs` ·
`tests/Negosio.UnitTests/Branches/BranchAccessResolverTests.cs` ·
`tests/Negosio.IntegrationTests/Branches/BranchManagementTests.cs` ·
`tests/Negosio.IntegrationTests/Branches/BranchAuthTests.cs` ·
`tests/Negosio.IntegrationTests/Branches/BranchScopedAccessTests.cs` ·
`tests/Negosio.IntegrationTests/Branches/RegisterSessionModelTests.cs` ·
`tests/Negosio.IntegrationTests/Branches/StaffBranchTests.cs` ·
`tests/Negosio.IntegrationTests/Branches/PosContextTests.cs`

Modified: `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs` (helpers).

---

## Task 1: Branch domain + roles + error codes + management service

**Files:**
- Modify: `src/Negosio.Domain/Entities/Branch.cs`
- Create: `src/Negosio.Application/Branches/BranchRoles.cs`
- Modify: `src/Negosio.Application/Common/ErrorCodes.cs`
- Modify: `src/Negosio.Application/Branches/BranchContracts.cs`
- Create: `src/Negosio.Application/Branches/BranchValidators.cs`
- Create: `src/Negosio.Application/Branches/BranchManagementService.cs`
- Modify: `src/Negosio.Application/Branches/BranchQueryService.cs`
- Modify: `src/Negosio.Application/DependencyInjection.cs`
- Test: `tests/Negosio.UnitTests/Branches/BranchDomainTests.cs`,
  `tests/Negosio.UnitTests/Branches/BranchRolesTests.cs`

**Interfaces:**
- Produces: `Branch.UpdateDetails(string,string,string?,string,string,string?)`,
  `Branch.Deactivate()`, `Branch.Reactivate()`.
- Produces: `BranchRoles.IsAllBranch(UserRole) : bool`,
  `BranchRoles.BranchScoped : IReadOnlySet<UserRole>`.
- Produces: `BranchDto(Guid Id, string Name, string Code, string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode, bool IsActive, DateTime CreatedAtUtc)`.
- Produces: `CreateBranchRequest(string Name, string Code, string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode)`,
  `UpdateBranchRequest(string Name, string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode)`.
- Produces: `IBranchManagementService { GetAsync(Guid), CreateAsync(CreateBranchRequest), UpdateAsync(Guid, UpdateBranchRequest), DeactivateAsync(Guid), ReactivateAsync(Guid) }` — every method returns `Task<BranchDto>`.
- Produces (error codes): `ErrorCodes.LastActiveBranch = "LAST_ACTIVE_BRANCH"`,
  `BranchInactive = "BRANCH_INACTIVE"`, `BranchForbidden = "BRANCH_FORBIDDEN"`,
  `SessionNotOwned = "SESSION_NOT_OWNED"`, `CashierSessionOpen = "CASHIER_SESSION_OPEN"`,
  `StaffHasOpenRegisterSession = "STAFF_HAS_OPEN_REGISTER_SESSION"`.

- [ ] **Step 1: Unit test — Branch domain methods**

Create `tests/Negosio.UnitTests/Branches/BranchDomainTests.cs`:

```csharp
using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Branches;

public class BranchDomainTests
{
    private static Branch NewBranch() =>
        Branch.Create(Guid.NewGuid(), "Main", "main", "L1", null, "City", "Province", "1000");

    [Fact]
    public void UpdateDetails_trims_and_keeps_the_code()
    {
        var b = NewBranch();
        var before = b.UpdatedAtUtc;

        b.UpdateDetails("  BGC Hub ", " New L1 ", "  ", " Taguig ", " Metro Manila ", "  ");

        b.Name.Should().Be("BGC Hub");
        b.AddressLine1.Should().Be("New L1");
        b.AddressLine2.Should().BeNull();
        b.City.Should().Be("Taguig");
        b.PostalCode.Should().BeNull();
        b.Code.Should().Be("MAIN"); // unchanged
        b.UpdatedAtUtc.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Deactivate_then_Reactivate_toggles_IsActive_and_touches()
    {
        var b = NewBranch();
        b.IsActive.Should().BeTrue();

        b.Deactivate();
        b.IsActive.Should().BeFalse();

        b.Reactivate();
        b.IsActive.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run — expect compile failure (methods don't exist)**

Run: `dotnet test tests/Negosio.UnitTests/Negosio.UnitTests.csproj --filter FullyQualifiedName~BranchDomainTests`
Expected: build error — `Branch` has no `UpdateDetails`/`Deactivate`/`Reactivate`.

- [ ] **Step 3: Add the domain methods**

In `src/Negosio.Domain/Entities/Branch.cs`, after the `Create` method, add:

```csharp
/// <summary>Editable branch details. The <see cref="Code"/> is immutable after creation.</summary>
public void UpdateDetails(
    string name, string addressLine1, string? addressLine2, string city, string province, string? postalCode)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        throw new ArgumentException("Branch name is required.", nameof(name));
    }

    Name = name.Trim();
    AddressLine1 = addressLine1?.Trim() ?? string.Empty;
    AddressLine2 = string.IsNullOrWhiteSpace(addressLine2) ? null : addressLine2.Trim();
    City = city?.Trim() ?? string.Empty;
    Province = province?.Trim() ?? string.Empty;
    PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
    Touch();
}

public void Deactivate()
{
    IsActive = false;
    Touch();
}

public void Reactivate()
{
    IsActive = true;
    Touch();
}
```

- [ ] **Step 4: Run — BranchDomainTests pass**

Run the same filter. Expected: PASS.

- [ ] **Step 5: Unit test — BranchRoles**

Create `tests/Negosio.UnitTests/Branches/BranchRolesTests.cs`:

```csharp
using FluentAssertions;
using Negosio.Application.Branches;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Branches;

public class BranchRolesTests
{
    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, false)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.InventoryStaff, false)]
    [InlineData(UserRole.KitchenStaff, false)]
    [InlineData(UserRole.Viewer, false)]
    public void IsAllBranch_is_true_only_for_Owner_and_Admin(UserRole role, bool expected)
        => BranchRoles.IsAllBranch(role).Should().Be(expected);

    [Fact]
    public void BranchScoped_is_every_non_owner_admin_role()
        => BranchRoles.BranchScoped.Should().BeEquivalentTo(new[]
        {
            UserRole.Manager, UserRole.Cashier, UserRole.InventoryStaff,
            UserRole.KitchenStaff, UserRole.Viewer
        });
}
```

- [ ] **Step 6: Create `BranchRoles`**

`src/Negosio.Application/Branches/BranchRoles.cs`:

```csharp
using Negosio.Domain.Enums;

namespace Negosio.Application.Branches;

/// <summary>
/// Owner/Admin are tenant-wide; every other role is bound to exactly one branch.
/// </summary>
public static class BranchRoles
{
    public static readonly IReadOnlySet<UserRole> BranchScoped = new HashSet<UserRole>
    {
        UserRole.Manager, UserRole.Cashier, UserRole.InventoryStaff,
        UserRole.KitchenStaff, UserRole.Viewer
    };

    public static bool IsAllBranch(UserRole role) => role is UserRole.Owner or UserRole.Admin;
}
```

- [ ] **Step 7: Run — BranchRolesTests pass**

Run: `dotnet test tests/Negosio.UnitTests/Negosio.UnitTests.csproj --filter FullyQualifiedName~BranchRolesTests`

- [ ] **Step 8: Add error codes**

In `src/Negosio.Application/Common/ErrorCodes.cs`, add a `// ---- Phase 5: Branch management ----`
section:

```csharp
public const string LastActiveBranch = "LAST_ACTIVE_BRANCH";
public const string BranchInactive = "BRANCH_INACTIVE";
public const string BranchForbidden = "BRANCH_FORBIDDEN";
public const string SessionNotOwned = "SESSION_NOT_OWNED";
public const string CashierSessionOpen = "CASHIER_SESSION_OPEN";
public const string StaffHasOpenRegisterSession = "STAFF_HAS_OPEN_REGISTER_SESSION";
```

(Reuse the existing `BranchNotFound`, `DuplicateBranchCode`, `RegisterSessionAlreadyOpen`,
`SaleNotFound`.)

- [ ] **Step 9: Rewrite `BranchContracts.cs`**

Replace the file with the widened DTO + request records + both interfaces (keep
`IBranchQueryService`):

```csharp
namespace Negosio.Application.Branches;

public sealed record BranchDto(
    Guid Id, string Name, string Code,
    string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode,
    bool IsActive, DateTime CreatedAtUtc);

public sealed record CreateBranchRequest(
    string Name, string Code,
    string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode);

public sealed record UpdateBranchRequest(
    string Name,
    string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode);

public interface IBranchQueryService
{
    /// <summary>
    /// Branch selector feed. Owner/Admin: active branches (or all when <paramref name="includeInactive"/>).
    /// Branch-scoped users: always exactly their assigned branch.
    /// </summary>
    Task<IReadOnlyList<BranchDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
}

public interface IBranchManagementService
{
    Task<BranchDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken = default);
    Task<BranchDto> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken = default);
    Task<BranchDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BranchDto> ReactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 10: Create `BranchValidators.cs`**

Mirror `src/Negosio.Application/Registers/RegisterValidators.cs` structure. Rules:
`Name` NotEmpty MaximumLength(150); `Code` (create only) NotEmpty MaximumLength(20)
`Matches("^[A-Za-z0-9-]+$")` with message "Use letters, numbers and hyphens only.";
`AddressLine1` NotEmpty MaximumLength(200); `City` NotEmpty MaximumLength(100);
`Province` NotEmpty MaximumLength(100); `AddressLine2` MaximumLength(200);
`PostalCode` MaximumLength(20). Classes: `CreateBranchRequestValidator`,
`UpdateBranchRequestValidator`.

- [ ] **Step 11: Create `BranchManagementService.cs`**

Mirror `RegisterService` (tenant resolution via `ICurrentUser`, `ValidateAndThrowAppAsync`,
`SqlUniqueViolation` translation). Key logic:

```csharp
public async Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken ct = default)
{
    var tenantId = RequireTenant();
    await _createValidator.ValidateAndThrowAppAsync(request, ct);

    var branch = Branch.Create(tenantId, request.Name, request.Code, request.AddressLine1,
        request.AddressLine2, request.City, request.Province, request.PostalCode);
    _db.Branches.Add(branch);

    try { await _db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
    {
        throw new ConflictException(ErrorCodes.DuplicateBranchCode, "A branch with this code already exists.");
    }
    return await GetAsync(branch.Id, ct);
}

public async Task<BranchDto> DeactivateAsync(Guid id, CancellationToken ct = default)
{
    var tenantId = RequireTenant();
    var branch = await _db.Branches.SingleOrDefaultAsync(b => b.TenantId == tenantId && b.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.BranchNotFound, "Branch not found.");

    if (branch.IsActive)
    {
        var otherActive = await _db.Branches.CountAsync(b => b.TenantId == tenantId && b.IsActive && b.Id != id, ct);
        if (otherActive == 0)
        {
            throw new BusinessRuleException(ErrorCodes.LastActiveBranch,
                "You can't deactivate the last active branch.");
        }
    }

    branch.Deactivate();
    await _db.SaveChangesAsync(ct);
    return await GetAsync(id, ct);
}
```

`GetAsync` projects the full `BranchDto`; 404 (`BranchNotFound`) when not in tenant.
`UpdateAsync` → load in-tenant, `branch.UpdateDetails(...)`, save. `ReactivateAsync` → load,
`branch.Reactivate()`, save.

- [ ] **Step 12: Widen `BranchQueryService.ListAsync` projection**

Change the `.Select(...)` to the full `BranchDto`. (The Owner/Admin-vs-scoped behaviour of
§6.1 is added in Task 6 once `BranchAccessResolver` exists — for now keep the current
"active unless includeInactive" filter and leave a `// TODO(task-6): scoped users see only their branch`.)

- [ ] **Step 13: Register DI**

In `src/Negosio.Application/DependencyInjection.cs`, under a Phase 5 comment:
`services.AddScoped<IBranchManagementService, BranchManagementService>();`

- [ ] **Step 14: Build + full unit suite**

Run: `dotnet build Negosio.sln` then
`dotnet test tests/Negosio.UnitTests/Negosio.UnitTests.csproj`
Expected: build clean; unit tests green (was 76, now ~80).

- [ ] **Step 15: Commit**

```bash
git add -A
git commit -m "feat(branches): branch domain methods + management service + error codes"
```

---

## Task 2: Branch CRUD API + BranchManage policy

**Files:**
- Modify: `src/Negosio.Api/Authorization/AuthorizationPolicies.cs`
- Modify: `src/Negosio.Api/Controllers/BranchesController.cs`
- Test: `tests/Negosio.IntegrationTests/Branches/BranchManagementTests.cs`
- Modify: `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs`

**Interfaces:**
- Consumes: `IBranchManagementService`, `IBranchQueryService` (Task 1).
- Produces: `AuthorizationPolicies.BranchManage = "BranchManage"` (Owner/Admin).
- Produces: routes `GET /api/branches?includeInactive=`, `GET /api/branches/{id:guid}`,
  `POST /api/branches`, `PUT /api/branches/{id:guid}`,
  `POST /api/branches/{id:guid}/deactivate`, `POST /api/branches/{id:guid}/reactivate`.
- Produces (test helper): `IntegrationTest.CreateBranchAsync(string name, string code, ...)` → `Task<BranchDto>`.

- [ ] **Step 1: Add the policy**

In `AuthorizationPolicies.cs`: add `public const string BranchManage = "BranchManage";` in
the Phase 5 area and, in `AddNegosioPolicies`:

```csharp
options.AddPolicy(BranchManage, policy =>
    policy.RequireAuthenticatedUser()
          .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(OwnerAdmin)));
```

- [ ] **Step 2: Add the test helper**

In `IntegrationTest.cs` add:

```csharp
protected async Task<BranchDto> CreateBranchAsync(
    string name = "BGC", string code = "BGC",
    string city = "Taguig", string province = "Metro Manila")
{
    var response = await Client.PostAsJsonAsync("/api/branches", new CreateBranchRequest(
        name, code, "5th Ave", null, city, province, "1634"));
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<BranchDto>(TestJson.Options))!;
}
```

(add `using Negosio.Application.Branches;`)

- [ ] **Step 3: Write `BranchManagementTests`**

Create `tests/Negosio.IntegrationTests/Branches/BranchManagementTests.cs` with the cases
from spec §11.1 "BranchManagementTests":
- `Owner_creates_a_branch` — POST → 201/200, then `GET /api/branches?includeInactive=false`
  contains it; `GET /api/branches/{id}` returns full detail.
- `Duplicate_code_is_a_clean_conflict` — second POST with same code → 409 `DUPLICATE_BRANCH_CODE`.
- `Only_Owner_and_Admin_may_create_a_branch` — `[Theory]` over roles via
  `AddTenantUserTokenAsync`; Owner/Admin → 200, others → 403.
- `A_tenant_cannot_touch_another_tenants_branch` — create in A, use B's token → GET/PUT/
  deactivate/reactivate all 404.
- `Update_changes_name_and_address_but_not_code` — PUT new name; GET shows new name, same code.
- `Deactivating_one_of_two_keeps_it_queryable` — create 2nd, deactivate it, `includeInactive=true`
  still lists it, `includeInactive=false` does not.
- `The_last_active_branch_cannot_be_deactivated` — single branch → deactivate → 409 `LAST_ACTIVE_BRANCH`.
- `Reactivate_restores_active` — deactivate 2nd then reactivate → active.

Use `RegisterLoginAndAuthorizeAsync()` for the Owner; `AddTenantUserTokenAsync(email, role)`
for the matrix. Follow the assertion style in `tests/Negosio.IntegrationTests/Catalog/BranchTests.cs`.

- [ ] **Step 4: Run — expect failures (endpoints 404 / helper compile)**

Run: `dotnet test tests/Negosio.IntegrationTests/Negosio.IntegrationTests.csproj --filter FullyQualifiedName~BranchManagementTests`
Expected: FAIL (routes don't exist yet).

- [ ] **Step 5: Rewrite `BranchesController.cs`**

```csharp
[ApiController]
[Authorize]
[Route("api/branches")]
public sealed class BranchesController : ControllerBase
{
    private readonly IBranchQueryService _query;
    private readonly IBranchManagementService _management;

    public BranchesController(IBranchQueryService query, IBranchManagementService management)
    {
        _query = query;
        _management = management;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BranchDto>>> List(
        [FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await _query.ListAsync(includeInactive, ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    public async Task<ActionResult<BranchDto>> Get(Guid id, CancellationToken ct)
        => Ok(await _management.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    public async Task<ActionResult<BranchDto>> Create([FromBody] CreateBranchRequest request, CancellationToken ct)
    {
        var branch = await _management.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = branch.Id }, branch);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    public async Task<ActionResult<BranchDto>> Update(Guid id, [FromBody] UpdateBranchRequest request, CancellationToken ct)
        => Ok(await _management.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    public async Task<ActionResult<BranchDto>> Deactivate(Guid id, CancellationToken ct)
        => Ok(await _management.DeactivateAsync(id, ct));

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    public async Task<ActionResult<BranchDto>> Reactivate(Guid id, CancellationToken ct)
        => Ok(await _management.ReactivateAsync(id, ct));
}
```

- [ ] **Step 6: Run — BranchManagementTests pass**

Run the filter from Step 4. Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(branches): branch CRUD API + BranchManage policy + tests"
```

---

## Task 3: `/branches` frontend + capability-gated nav

**Files:**
- Create: `src/api/branches.ts`, `src/pages/BranchesPage.tsx`,
  `src/components/branches/BranchFormModal.tsx`, `src/components/branches/BranchStatusBadge.tsx`
- Modify: `src/api/types.ts`, `src/api/inventory.ts`, `src/lib/useCan.ts`, `src/lib/nav.ts`,
  `src/App.tsx`

- [ ] **Step 1: Types + API module**

`src/api/types.ts` — widen `BranchDto` to match the backend record; add `CreateBranchRequest`,
`UpdateBranchRequest`.

`src/api/branches.ts`:

```ts
import { apiRequest } from './client'
import { qs } from './query-string'
import type { BranchDto, CreateBranchRequest, UpdateBranchRequest } from './types'

export const branchesApi = {
  list: (params: { includeInactive?: boolean } = {}) =>
    apiRequest<BranchDto[]>(`/api/branches${qs({ ...params })}`),
  get: (id: string) => apiRequest<BranchDto>(`/api/branches/${id}`),
  create: (body: CreateBranchRequest) =>
    apiRequest<BranchDto>('/api/branches', { method: 'POST', body }),
  update: (id: string, body: UpdateBranchRequest) =>
    apiRequest<BranchDto>(`/api/branches/${id}`, { method: 'PUT', body }),
  deactivate: (id: string) =>
    apiRequest<BranchDto>(`/api/branches/${id}/deactivate`, { method: 'POST' }),
  reactivate: (id: string) =>
    apiRequest<BranchDto>(`/api/branches/${id}/reactivate`, { method: 'POST' }),
}
```

`src/api/inventory.ts` — replace its local `branchesApi` with
`export { branchesApi } from './branches'` (keep the import site working). Verify every
existing `branchesApi.list()` call still compiles (it now takes an optional arg — fine).

- [ ] **Step 2: Capability + nav + route**

`src/lib/useCan.ts` — add `'branch:manage'` to `Capability` and
`'branch:manage': new Set<UserRole>(['Owner', 'Admin'])` to `CAPABILITY_ROLES`.

`src/lib/nav.ts` — import `Building2` from lucide-react; add to the Staff/Settings group,
before Settings: `{ label: 'Branches', icon: Building2, to: '/branches', enabled: true, capability: 'branch:manage' }`.

`src/App.tsx` — import `BranchesPage`; add to `protectedRoutes`:
```tsx
{ path: '/branches', element: (
    <RequireCapability capability="branch:manage" title="Branches"><BranchesPage /></RequireCapability>
  ) },
```

- [ ] **Step 3: `BranchStatusBadge` + `BranchFormModal`**

`BranchStatusBadge.tsx` — mirror `src/components/staff/StaffBadges.tsx`
(`Active` → `success`, `Inactive` → `neutral`).

`BranchFormModal.tsx` — mirror `src/components/registers/RegisterFormModal.tsx`. Fields:
Name, Code (create only; on edit render read-only `TextField` with `hint="Branch codes can't be changed."`),
AddressLine1, AddressLine2, City, Province, PostalCode. On success invalidate
`['branches']` and `['branches','manage']`. Translate a 409 `DUPLICATE_BRANCH_CODE` to a
`codeError`.

- [ ] **Step 4: `BranchesPage.tsx`**

Mirror `src/pages/StaffPage.tsx` shape (DashboardLayout, header + "Add branch" button,
SearchInput + status `Select`). Query:
`useQuery({ queryKey: ['branches','manage', { includeInactive }], queryFn: () => branchesApi.list({ includeInactive: true }) })`
then client-filter by status + search (small dataset). Table columns: Name, Code,
`{city}, {province}`, `<BranchStatusBadge>`, actions: Edit (opens modal), Deactivate/Reactivate
via `ConfirmDialog`. Deactivate dialog message: *"Staff assigned to this branch will lose
access until you reactivate it or reassign them. Historical data is kept."* Mutations →
`onError` toast `err.message` (this cleanly surfaces `LAST_ACTIVE_BRANCH`).

- [ ] **Step 5: Lint + build**

Run: `cd web/negosio-web && npm run lint && npm run build`
Expected: clean.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(branches): /branches page, form modal, capability-gated nav"
```

---

## Task 4: `User.BranchId` + `StaffInvitation.BranchId` + migrations

**Files:**
- Modify: `src/Negosio.Domain/Entities/User.cs`,
  `src/Negosio.Domain/Entities/Platform/StaffInvitation.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/UserConfiguration.cs`,
  `src/Negosio.Infrastructure/Persistence/Platform/Configurations/StaffInvitationConfiguration.cs`
- Create (generated): tenant migration `AddUserBranch`, platform migration `AddStaffInvitationBranch`
- Test: `tests/Negosio.UnitTests/Branches/BranchDomainTests.cs` (extend)

**Interfaces:**
- Produces: `User.AssignBranch(Guid)`, `User.ClearBranch()`, `User.BranchId : Guid?`,
  `User.Create(..., Guid? branchId = null)`.
- Produces: `StaffInvitation.BranchId : Guid?`, `StaffInvitation.Create(..., Guid? branchId = null)`.

- [ ] **Step 1: Unit test — User branch assignment**

Add to `BranchDomainTests.cs`:

```csharp
[Fact]
public void User_AssignBranch_then_ClearBranch()
{
    var u = Negosio.Domain.Entities.User.Create(
        Guid.NewGuid(), Guid.NewGuid(), "a@b.com", "A", "B", Negosio.Domain.Enums.UserRole.Cashier);
    u.BranchId.Should().BeNull();

    var branchId = Guid.NewGuid();
    u.AssignBranch(branchId);
    u.BranchId.Should().Be(branchId);

    u.ClearBranch();
    u.BranchId.Should().BeNull();
}
```

- [ ] **Step 2: Add `User.BranchId` + methods**

In `User.cs`: add `public Guid? BranchId { get; private set; }`; add optional
`Guid? branchId = null` to the private ctor and `Create` (set `BranchId = branchId`); add:

```csharp
public void AssignBranch(Guid branchId)
{
    BranchId = branchId;
    Touch();
}

public void ClearBranch()
{
    BranchId = null;
    Touch();
}
```

- [ ] **Step 3: Add `StaffInvitation.BranchId`**

In `StaffInvitation.cs`: add `public Guid? BranchId { get; private set; }`; thread
`Guid? branchId = null` through the private ctor + `Create` (`BranchId = branchId`).

- [ ] **Step 4: EF configuration**

`UserConfiguration.cs` — add:
```csharp
builder.Property(u => u.BranchId);
builder.HasOne<Branch>().WithMany().HasForeignKey(u => u.BranchId).OnDelete(DeleteBehavior.Restrict);
builder.HasIndex(u => u.BranchId).HasDatabaseName("IX_Users_BranchId");
```
`StaffInvitationConfiguration.cs` — add `builder.Property(i => i.BranchId);` (no FK — cross-DB).

- [ ] **Step 5: Generate the tenant migration**

Run:
```
dotnet dotnet-ef migrations add AddUserBranch --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant
```
Then **edit the generated `Up()`** to append the backfill after the `AddColumn`/`CreateIndex`:
```csharp
migrationBuilder.Sql(
    "UPDATE Users SET BranchId = (SELECT TOP 1 Id FROM Branches) WHERE [Role] NOT IN (1, 2);");
```
(Owner=1, Admin=2. Every current tenant has exactly one branch.)

- [ ] **Step 6: Generate the platform migration**

Run:
```
dotnet dotnet-ef migrations add AddStaffInvitationBranch --context PlatformDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Platform
```
No hand-edit needed (nullable column).

- [ ] **Step 7: Build + unit tests + a smoke migration run**

Run: `dotnet build Negosio.sln` and `dotnet test tests/Negosio.UnitTests/Negosio.UnitTests.csproj`.
Then run the full integration suite once to confirm the tenant DBs still migrate from scratch:
`dotnet test tests/Negosio.IntegrationTests/Negosio.IntegrationTests.csproj --filter FullyQualifiedName~RegistrationTests`
Expected: green (provisioning applies the new migration).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(staff): User.BranchId + StaffInvitation.BranchId + backfill migration"
```

---

## Task 5: `BranchAccessResolver` + `GET /api/branches` scoping

**Files:**
- Create: `src/Negosio.Application/Branches/BranchAccessResolver.cs`
- Modify: `src/Negosio.Application/Branches/BranchQueryService.cs`,
  `src/Negosio.Application/DependencyInjection.cs`
- Test: `tests/Negosio.UnitTests/Branches/BranchAccessResolverTests.cs`,
  `tests/Negosio.IntegrationTests/Branches/BranchScopedAccessTests.cs` (start — branches list only)

**Interfaces:**
- Consumes: `ICurrentUser`, `ITenantDbContext`, `BranchRoles`.
- Produces: `IBranchAccessResolver` (§4):
  `Task<Guid?> AssignedBranchIdAsync(CancellationToken)`,
  `Task<Guid?> ResolveListFilterAsync(Guid? requested, CancellationToken)`,
  `Task<Guid> ResolveTargetBranchAsync(Guid? requested, bool allowInactive = false, CancellationToken)`,
  `bool IsAllBranch { get; }`.

- [ ] **Step 1: Create `BranchAccessResolver`**

```csharp
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;

namespace Negosio.Application.Branches;

public interface IBranchAccessResolver
{
    bool IsAllBranch { get; }
    Task<Guid?> AssignedBranchIdAsync(CancellationToken ct = default);
    Task<Guid?> ResolveListFilterAsync(Guid? requested, CancellationToken ct = default);
    Task<Guid> ResolveTargetBranchAsync(Guid? requested, bool allowInactive = false, CancellationToken ct = default);
}

public sealed class BranchAccessResolver : IBranchAccessResolver
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private Guid? _assigned;
    private bool _loaded;

    public BranchAccessResolver(ITenantDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public bool IsAllBranch => BranchRoles.IsAllBranch(_currentUser.Role);

    public async Task<Guid?> AssignedBranchIdAsync(CancellationToken ct = default)
    {
        if (IsAllBranch) return null;
        if (_loaded) return _assigned;

        _assigned = await _db.Users.AsNoTracking()
            .Where(u => u.Id == _currentUser.UserId)
            .Select(u => u.BranchId)
            .FirstOrDefaultAsync(ct);
        _loaded = true;

        if (_assigned is null)
        {
            // A branch-scoped account with no assignment cannot act anywhere.
            throw new ForbiddenAppException(ErrorCodes.BranchForbidden,
                "Your account is not assigned to a branch. Ask an administrator.");
        }
        return _assigned;
    }

    public async Task<Guid?> ResolveListFilterAsync(Guid? requested, CancellationToken ct = default)
        => IsAllBranch ? requested : await AssignedBranchIdAsync(ct);

    public async Task<Guid> ResolveTargetBranchAsync(Guid? requested, bool allowInactive = false, CancellationToken ct = default)
    {
        Guid target;
        if (IsAllBranch)
        {
            target = requested ?? throw new BusinessRuleException(ErrorCodes.BranchNotFound, "A branch is required.");
        }
        else
        {
            var assigned = (await AssignedBranchIdAsync(ct))!.Value;
            if (requested is { } r && r != assigned)
            {
                throw new ForbiddenAppException(ErrorCodes.BranchForbidden, "You can only work in your assigned branch.");
            }
            target = assigned;
        }

        var branch = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == _currentUser.TenantId && b.Id == target)
            .Select(b => new { b.IsActive })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ErrorCodes.BranchNotFound, "Branch not found.");

        if (!allowInactive && !branch.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.BranchInactive, "This branch is inactive.");
        }
        return target;
    }
}
```

Register in `DependencyInjection.cs`: `services.AddScoped<IBranchAccessResolver, BranchAccessResolver>();`

- [ ] **Step 2: Unit tests for the resolver**

`BranchAccessResolverTests.cs` — use a fake `ICurrentUser` and an in-memory / SQLite
`TenantDbContext` (see how other unit tests build a context, or use a small hand-rolled
`ITenantDbContext` fake if one exists). Cases:
- Owner → `IsAllBranch` true; `ResolveListFilterAsync(x)` returns `x`;
  `ResolveTargetBranchAsync(activeBranchId)` returns it; `ResolveTargetBranchAsync(null)` throws.
- Cashier assigned B → `ResolveListFilterAsync(anything)` returns B;
  `ResolveTargetBranchAsync(B)` returns B; `ResolveTargetBranchAsync(otherId)` →
  `ForbiddenAppException` `BRANCH_FORBIDDEN`; `ResolveTargetBranchAsync(B)` when B inactive →
  `BusinessRuleException` `BRANCH_INACTIVE`.

If wiring a real `TenantDbContext` in a unit test is awkward, make these **integration**
tests in `BranchScopedAccessTests` instead and keep the unit file to the pure `IsAllBranch`
mapping. (Do not build a fake-repository just for this.)

- [ ] **Step 3: Scope `GET /api/branches`**

In `BranchQueryService.ListAsync`, inject `IBranchAccessResolver`. At the top:

```csharp
var assigned = await _resolver.AssignedBranchIdAsync(cancellationToken); // null for Owner/Admin
if (assigned is { } b)
{
    return await _db.Branches.AsNoTracking()
        .Where(x => x.TenantId == tenantId && x.Id == b)
        .Select(Projection)
        .ToListAsync(cancellationToken);
}
// Owner/Admin: existing active-or-all behaviour
```

Remove the `// TODO(task-6)` left in Task 1.

- [ ] **Step 4: Integration test — branch list scoping**

Create `BranchScopedAccessTests.cs`, first case
`A_branch_scoped_user_only_sees_their_own_branch`:
- Owner registers (branch MAIN), creates branch BGC.
- Invite path isn't ready yet — instead seed a Cashier via `AddTenantUserTokenAsync` and, in
  an `InScopeAsync`, set that user's `BranchId = bgcId` (`user.AssignBranch(bgcId)`).
- With the cashier token: `GET /api/branches` → exactly `[BGC]`.
- With the owner token: `GET /api/branches?includeInactive=true` → `[MAIN, BGC]`.

Extend `AddTenantUserTokenAsync` now: add an optional `Guid? branchId = null` param; after
creating the tenant `User`, if `branchId` is set call `user.AssignBranch(branchId.Value)`
before `SaveChangesAsync`. (Owner/Admin callers pass null.)

- [ ] **Step 5: Run + build**

`dotnet build Negosio.sln` · unit + the new integration filter. Expected green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(branches): BranchAccessResolver + scoped GET /api/branches"
```

---

## Task 6: Branch-scoped enforcement across every branch-aware service

**Files:**
- Modify: `InventoryService.cs`, `InventoryContracts.cs` (no shape change — resolver call),
  `RegisterService.cs`, `SaleQueryService.cs`, `ReceiptService.cs`, `ReturnService.cs`,
  `PosCatalogService.cs`, `CheckoutService.cs`, `DashboardService.cs`
- Test: `tests/Negosio.IntegrationTests/Branches/BranchScopedAccessTests.cs` (complete it)

**Interfaces:**
- Consumes: `IBranchAccessResolver` (Task 5), injected into each service's ctor.

Apply the §8 table. For each service, inject `IBranchAccessResolver _resolver` and:

- [ ] **Step 1: Test-first — the scoped-access matrix**

Complete `BranchScopedAccessTests.cs` with the spec §11.1 "BranchScopedAccessTests" cases.
Setup helper: Owner registers (MAIN), creates BGC, seeds a stocked product at each branch,
opens a register+session+sale at MAIN; then a Cashier bound to BGC via
`AddTenantUserTokenAsync(email, Cashier, branchId: bgcId)`. Assertions (cashier token):
- `GET /api/inventory` → rows only where `branchId == bgc`.
- `POST /api/inventory/adjustments` `{ branchId: mainId, ... }` → 403 `BRANCH_FORBIDDEN`.
- `GET /api/sales` → no MAIN sales; `GET /api/sales?branchId=<mainId>` → still no MAIN sales.
- `GET /api/sales/{mainSaleId}` → 404; `/receipt` → 404.
- `POST /api/pos/checkout` `{ branchId: mainId, ... }` → 403.
- `GET /api/pos/catalog?branchId=<mainId>` → 403.
- Owner token: `GET /api/inventory` → MAIN + BGC rows; adjust either → 200.
- `Multi_branch_inventory_isolation` (opening 10 / 3, sell 2 at MAIN → 8 / 3).
- `Per_branch_document_numbering` (MAIN 00000001, BGC 00000001, MAIN return 00000002, BGC 00000002).
- `Return_restock_only_touches_the_sale_branch`.

Run → FAIL.

- [ ] **Step 2: `DashboardService`**

Inject resolver. Replace the five tenant-wide aggregates so each also filters
`await _resolver.ResolveListFilterAsync(null, ct)` when non-null:
- `lowStockItems`: `.Where(i => i.BranchId == branchFilter)` when set.
- today's sales query: `.Where(s => s.BranchId == branchFilter)` when set.
- `branchCount`: `b.TenantId == tenantId && b.IsActive` (active-only); when `branchFilter`
  set, it is `1`.

- [ ] **Step 3: `InventoryService`**

- `ListAsync`: `query = query with { BranchId = await _resolver.ResolveListFilterAsync(query.BranchId, ct) }`
  before applying the existing `if (query.BranchId is …)` filter.
- `ListMovementsAsync`: same.
- `AdjustAsync`: replace the `_db.Branches.SingleOrDefaultAsync(...)` existence check with
  `var branchId = await _resolver.ResolveTargetBranchAsync(request.BranchId, ct);` then load
  the `Branch` by that id. (`ResolveTargetBranchAsync` already 404s a missing branch and
  409s an inactive one.)

- [ ] **Step 4: `RegisterService`**

- `ListAsync`: `query = query with { BranchId = await _resolver.ResolveListFilterAsync(query.BranchId, ct) }`.
- `CreateAsync`: replace the `branchExists` check with
  `var branchId = await _resolver.ResolveTargetBranchAsync(request.BranchId, ct);` and use
  `branchId` for `Register.Create`.

- [ ] **Step 5: `PosCatalogService`**

- `SearchAsync`: `var branchId = await _resolver.ResolveTargetBranchAsync(query.BranchId, ct);`
  (replaces `RequireBranchAsync` + `query.BranchId`).
- `BarcodeLookupAsync`: `var resolved = await _resolver.ResolveTargetBranchAsync(branchId, ct);`
  use `resolved`.
- Delete the now-unused `RequireBranchAsync`.

- [ ] **Step 6: `CheckoutService`**

Replace step 2's branch load:
```csharp
var branchId = await _resolver.ResolveTargetBranchAsync(request.BranchId, cancellationToken);
var branch = await _db.Branches.SingleAsync(b => b.TenantId == tenantId && b.Id == branchId, cancellationToken);
```
Keep the existing `session.BranchId != branch.Id` check. (Session ownership is Task 8.)

- [ ] **Step 7: `SaleQueryService` + `ReceiptService` + `ReturnService`**

- `SaleQueryService.ListAsync`: `query = query with { BranchId = await _resolver.ResolveListFilterAsync(query.BranchId, ct) }`.
- `SaleQueryService.GetAsync`: after loading `sale`,
  `if (!_resolver.IsAllBranch && sale.BranchId != await _resolver.AssignedBranchIdAsync(ct)) throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");`
- `ReceiptService.GetReceiptAsync`: same 404 guard after loading the sale.
- `ReturnService.CreateReturnAsync` + `ListReturnsAsync`: same guard after loading the sale —
  branch-scoped user gets 404 for a foreign sale. **No active-branch check** (Owner/Admin
  must refund against inactive branches). Restock stays `sale.BranchId`.

- [ ] **Step 8: Wire DI + run**

All these services are already `AddScoped` — just confirm the ctor changes compile. Run
`dotnet build Negosio.sln`, then the `BranchScopedAccessTests` filter, then the **full**
integration suite (`dotnet test tests/Negosio.IntegrationTests/...`) to catch regressions in
existing POS/inventory/sales tests (many seed a single branch — the resolver's Owner path
must leave them unaffected; where a test uses a non-Owner token it may now need a
`branchId` — fix those).

Expected: green. Note any pre-existing test that needed a `branchId` argument added.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(branches): enforce branch scope across inventory, registers, POS, sales, dashboard"
```

---

## Task 7: Authentication — branch-active enforcement

**Files:**
- Modify: `src/Negosio.Application/Auth/AuthService.cs`
- Create: `src/Negosio.Api/Middleware/BranchAccessMiddleware.cs`
- Modify: `src/Negosio.Api/Program.cs`
- Test: `tests/Negosio.IntegrationTests/Branches/BranchAuthTests.cs`

- [ ] **Step 1: Test-first — `BranchAuthTests`**

Create `BranchAuthTests.cs` with spec §11.1 "BranchAuthTests" cases. Helpers: register Owner
(MAIN), create BGC, seed a Cashier assigned BGC (via `AddTenantUserTokenAsync(..., branchId:
bgcId)` — but note that only mints a token; for the *login* cases you need a real
`PlatformUserLogin` with a known password. Add a helper
`AddLoginableTenantUserAsync(email, password, role, Guid? branchId)` that creates the
platform login with a real hash (use the DI `IPasswordHasher`) + the tenant `User` +
assignment, mirroring `AddTenantUserTokenAsync`).

Cases:
- `Cashier_with_an_active_branch_can_log_in`.
- `Cashier_with_an_inactive_branch_is_denied` → POST `/api/auth/login` → 400 `BRANCH_INACTIVE`.
- `Manager_with_an_inactive_branch_is_denied`.
- `Owner_logs_in_despite_inactive_branches`.
- `An_already_issued_token_stops_working_when_the_branch_is_deactivated`: login cashier
  (active) → capture token → Owner deactivates BGC → cashier calls `GET /api/dashboard` with
  the old token → 401 `BRANCH_INACTIVE`.
- `Reactivation_restores_access`.
- `Reassignment_to_an_active_branch_restores_access`.

Run → FAIL.

- [ ] **Step 2: Login check in `AuthService.LoginAsync`**

After the `!login.IsActive || tenant is null || !tenant.IsOperational` block, and after
`await using var tenantDb = …` is opened (move that `await using` up if needed, before token
generation is fine), add:

```csharp
if (!BranchRoles.IsAllBranch(login.Role))
{
    var branchActive = await tenantDb.Users.AsNoTracking()
        .Where(u => u.Id == login.Id)
        .Select(u => _dbBranchActive(tenantDb, u.BranchId))
        .FirstOrDefaultAsync(cancellationToken);

    if (branchActive != true)
    {
        throw new BusinessRuleException(ErrorCodes.BranchInactive,
            "Your assigned branch is currently inactive. Please contact your administrator.");
    }
}
```

Implement the branch-active lookup inline instead of a helper if cleaner:

```csharp
var assignment = await tenantDb.Users.AsNoTracking()
    .Where(u => u.Id == login.Id)
    .Select(u => new { u.BranchId })
    .SingleAsync(cancellationToken);

var branchActive = assignment.BranchId is { } bid
    && await tenantDb.Branches.AsNoTracking().AnyAsync(b => b.Id == bid && b.IsActive, cancellationToken);

if (!branchActive)
{
    throw new BusinessRuleException(ErrorCodes.BranchInactive,
        "Your assigned branch is currently inactive. Please contact your administrator.");
}
```

Add `using Negosio.Application.Branches;`.

- [ ] **Step 3: `BranchAccessMiddleware`**

```csharp
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;

namespace Negosio.Api.Middleware;

/// <summary>
/// After tenant routing: a branch-scoped user may continue only while their assigned branch is
/// active. Blocks a still-valid JWT the moment the branch is deactivated (401 BRANCH_INACTIVE).
/// Owner/Admin and anonymous requests pass straight through.
/// </summary>
public sealed class BranchAccessMiddleware
{
    private readonly RequestDelegate _next;

    public BranchAccessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, ITenantDbContext db)
    {
        if (!currentUser.IsAuthenticated || BranchRoles.IsAllBranch(currentUser.Role))
        {
            await _next(context);
            return;
        }

        var ok = await db.Users.AsNoTracking()
            .Where(u => u.Id == currentUser.UserId && u.BranchId != null)
            .AnyAsync(u => db.Branches.Any(b => b.Id == u.BranchId && b.IsActive), context.RequestAborted);

        if (!ok)
        {
            await Contracts.ApiErrorWriter.WriteAsync(context, StatusCodes.Status401Unauthorized,
                ErrorCodes.BranchInactive,
                "Your assigned branch is currently inactive. Please contact your administrator.");
            return;
        }

        await _next(context);
    }
}
```

If there is no reusable `ApiErrorWriter`, factor the small JSON writer out of
`ConfigureJwtBearerOptions.WriteErrorAsync` into
`src/Negosio.Api/Contracts/ApiErrorWriter.cs` (`static Task WriteAsync(HttpContext, int, string, string)`)
and call it from both places. Otherwise inline the same envelope shape (`ApiError`).

- [ ] **Step 4: Register the middleware**

In `Program.cs`, immediately after `app.UseMiddleware<TenantResolutionMiddleware>();`:
```csharp
app.UseMiddleware<BranchAccessMiddleware>();
```
(before `app.UseAuthorization();`).

- [ ] **Step 5: Run — `BranchAuthTests` pass + full suite**

Run the `BranchAuthTests` filter, then the full integration suite. Existing tests that log in
as a seeded non-Owner may now hit this — ensure such seeds assign an active branch (the
Task 5 change to `AddTenantUserTokenAsync` + backfill migration cover most; fix stragglers).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(auth): branch-scoped users require an active assigned branch to authenticate"
```

---

## Task 8: Register-session ownership + one-per-user + force-close

**Files:**
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/RegisterSessionConfiguration.cs`
- Create (generated): tenant migration `AddSessionOwnerOpenIndex` (or fold into Task 4's
  migration if Task 4 not yet applied to any real DB — but since Task 4 already committed,
  make a new migration)
- Modify: `src/Negosio.Application/Registers/RegisterSessionService.cs`,
  `RegisterContracts.cs`, `src/Negosio.Application/Pos/CheckoutService.cs`,
  `src/Negosio.Api/Controllers/RegisterSessionsController.cs`,
  `src/Negosio.Api/Authorization/AuthorizationPolicies.cs`,
  `src/Negosio.Application/Staff/StaffService.cs` (open-session guard for change-branch — but
  ChangeBranch itself lands in Task 10; add the helper here)
- Test: `tests/Negosio.IntegrationTests/Branches/RegisterSessionModelTests.cs`

**Interfaces:**
- Produces: `AuthorizationPolicies.RegisterForceClose = "RegisterForceClose"` (Owner/Admin).
- Produces: `IRegisterSessionService.ForceCloseAsync(Guid sessionId, CloseRegisterSessionRequest, CancellationToken)`.
- Produces (index): `IX_RegisterSessions_OpenedByUserId_Open` unique filtered on `(TenantId, OpenedByUserId) WHERE [Status] = 0`.

- [ ] **Step 1: Test-first — `RegisterSessionModelTests`**

Cases from spec §11.1 "RegisterSessionModelTests":
- `One_open_session_per_register` (two opens same register — 2nd → 409 `REGISTER_SESSION_ALREADY_OPEN`).
- `One_open_session_per_user` (same user opens a 2nd register → 409 `CASHIER_SESSION_OPEN`).
- `Checkout_through_another_users_session_is_forbidden` (403 `SESSION_NOT_OWNED`).
- `Closing_another_users_session_is_forbidden` (403).
- `Force_close_is_owner_admin_only` (Manager token → 403; Owner → 200).
- `Force_close_preserves_ownership_and_reconciliation` (`OpenedByUserId` unchanged,
  `ClosedByUserId` = owner, `CashDifference` computed).
- `Force_close_works_on_an_inactive_branch` (deactivate branch, owner force-closes).

Run → FAIL.

- [ ] **Step 2: The one-per-user index**

In `RegisterSessionConfiguration.cs` add:
```csharp
builder.HasIndex(s => new { s.TenantId, s.OpenedByUserId })
    .IsUnique()
    .HasFilter($"[Status] = {(int)RegisterSessionStatus.Open}")
    .HasDatabaseName("IX_RegisterSessions_OpenedByUserId_Open");
```
Generate: `dotnet dotnet-ef migrations add AddSessionOwnerOpenIndex --context TenantDbContext …`.

- [ ] **Step 3: `OpenAsync` — translate the new race**

In `RegisterSessionService.OpenAsync`, the `catch (DbUpdateException ex) when (SqlUniqueViolation…)`
block: branch on the constraint name:
```csharp
catch (DbUpdateException ex) when (SqlUniqueViolation.TryGetConstraintName(ex, out var name))
{
    if (name?.Contains("OpenedByUserId", StringComparison.OrdinalIgnoreCase) == true)
        throw new ConflictException(ErrorCodes.CashierSessionOpen,
            "You already have an open register session. Continue or close it first.");
    throw new ConflictException(ErrorCodes.RegisterSessionAlreadyOpen, "This register is already in use.");
}
```
Also add a pre-check for a friendlier path (optional): before `Add`, query
`_db.RegisterSessions.AnyAsync(s => s.TenantId == tenantId && s.OpenedByUserId == _currentUser.UserId && s.Status == Open)`.

- [ ] **Step 4: Ownership checks**

`RegisterSessionService.CloseAsync` — after loading `session`, before the status check:
```csharp
if (session.OpenedByUserId != _currentUser.UserId)
    throw new ForbiddenAppException(ErrorCodes.SessionNotOwned, "This register session belongs to another user.");
```
`CheckoutService.CheckoutAsync` — after loading `session`, alongside the branch check:
```csharp
if (session.OpenedByUserId != _currentUser.UserId)
    throw new ForbiddenAppException(ErrorCodes.SessionNotOwned, "This register session belongs to another user.");
```
`GetCurrentAsync` — add `&& s.OpenedByUserId == _currentUser.UserId` to the open-session query.

- [ ] **Step 5: `ForceCloseAsync` + policy + route**

`AuthorizationPolicies` — `RegisterForceClose` requiring `RoleNames(OwnerAdmin)`.

`RegisterContracts.IRegisterSessionService` — add
`Task<RegisterSessionDto> ForceCloseAsync(Guid sessionId, CloseRegisterSessionRequest request, CancellationToken cancellationToken = default);`

`RegisterSessionService.ForceCloseAsync` — copy `CloseAsync` but: **no** ownership check;
**no** branch-active requirement (load session by id in tenant, ignore branch state); same
reconciliation (`expected = OpeningCash + cashIn - cashOut`); `session.Close(_currentUser.UserId, …)`
so `ClosedByUserId` = the acting admin while `OpenedByUserId` is untouched.

`RegisterSessionsController` — add:
```csharp
[HttpPost("{id:guid}/force-close")]
[Authorize(Policy = AuthorizationPolicies.RegisterForceClose)]
public async Task<ActionResult<RegisterSessionDto>> ForceClose(
    Guid id, [FromBody] CloseRegisterSessionRequest request, CancellationToken ct)
    => Ok(await _sessions.ForceCloseAsync(id, request, ct));
```

Note: the controller is `[Authorize(Policy = PosOperate)]` at class level; the method-level
`RegisterForceClose` is *added*, and ASP.NET requires **both** — Owner/Admin satisfy both, so
that's correct. (Manager satisfies `PosOperate` but not `RegisterForceClose` → 403.)

- [ ] **Step 6: Add the open-session helper for later**

In `StaffService` add a private
`Task<bool> HasOpenSessionAsync(Guid userId, CancellationToken ct)` →
`_tenantDb.RegisterSessions.AnyAsync(s => s.OpenedByUserId == userId && s.Status == RegisterSessionStatus.Open, ct)`.
(Used by `ChangeBranchAsync` in Task 10.)

- [ ] **Step 7: Run — session tests + full suite**

Existing POS tests that open two sessions as the same seeded user will now fail with
`CASHIER_SESSION_OPEN` — audit `tests/Negosio.IntegrationTests/Pos/*` and give each session a
distinct opener where the test intent allows, or close between opens. Fix them.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(pos): one open session per user, session ownership, owner/admin force-close"
```

---

## Task 9: POS context + register-availability endpoints

**Files:**
- Create: `src/Negosio.Application/Pos/PosContextService.cs` (+ contracts)
- Modify: `src/Negosio.Api/Controllers/PosController.cs`,
  `src/Negosio.Application/DependencyInjection.cs`
- Test: `tests/Negosio.IntegrationTests/Branches/PosContextTests.cs`

**Interfaces:**
- Produces: `GET /api/pos/context` → `PosContextDto(Guid BranchId, string BranchName, bool CanPickBranch, IReadOnlyList<BranchDto> Branches)` — for a scoped user `BranchId` is theirs and `CanPickBranch=false`; for Owner/Admin with 1 active branch `CanPickBranch=false`; with ≥2, `CanPickBranch=true` and `Branches` = active branches (`BranchId`/`BranchName` = the first / a persisted hint is a frontend concern).
- Produces: `GET /api/pos/registers?branchId=` → `IReadOnlyList<PosRegisterDto>` where
  `PosRegisterDto(Guid Id, string Name, string Code, bool IsActive, PosOpenSessionDto? OpenSession)`,
  `PosOpenSessionDto(Guid SessionId, Guid OpenedByUserId, string OpenedByName, bool Mine)`.

- [ ] **Step 1: Test-first — `PosContextTests`**

- `Context_for_a_cashier_is_their_branch_no_picker`.
- `Context_for_an_owner_with_one_branch_has_no_picker`.
- `Context_for_an_owner_with_two_active_branches_offers_a_picker` (`Branches` length 2).
- `Registers_report_availability` — seed 3 registers in BGC; cashier A opens #1; call
  `GET /api/pos/registers?branchId=<bgc>` as cashier B → #1 has `OpenSession` with
  `Mine=false` + `OpenedByName`, #2/#3 `OpenSession=null`; as cashier A → #1 `Mine=true`.
- `Registers_reject_a_foreign_branch_for_a_scoped_user` → 403.

Run → FAIL.

- [ ] **Step 2: `PosContextService`**

Ctor: `ITenantDbContext`, `ICurrentUser`, `IBranchAccessResolver`. 

`GetContextAsync`:
- If scoped: `branchId = await resolver.AssignedBranchIdAsync()`; load name; return
  `CanPickBranch=false`, `Branches=[that one]`.
- If all-branch: load active branches ordered by name. `CanPickBranch = count >= 2`.
  `BranchId`/`BranchName` = first (frontend persists the real choice). `Branches` = all active.

`GetRegistersAsync(Guid? branchId)`:
- `var resolved = await resolver.ResolveTargetBranchAsync(branchId, ct);`
- Load active registers for `(tenantId, resolved)`, left-join the open `RegisterSession`
  (`Status == Open`) + opener name, project `PosRegisterDto` with
  `Mine = openSession.OpenedByUserId == currentUser.UserId`.

Register DI. Add both routes to `PosController` (class policy `PosOperate` already covers it):

```csharp
[HttpGet("context")]
public async Task<ActionResult<PosContextDto>> Context(CancellationToken ct)
    => Ok(await _context.GetContextAsync(ct));

[HttpGet("registers")]
public async Task<ActionResult<IReadOnlyList<PosRegisterDto>>> Registers(
    [FromQuery] Guid? branchId, CancellationToken ct)
    => Ok(await _context.GetRegistersAsync(branchId, ct));
```

- [ ] **Step 3: Run — `PosContextTests` pass**

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(pos): branch context + register-availability endpoints"
```

---

## Task 10: Staff ↔ branch — services, invitation, role/branch reconciliation

**Files:**
- Modify: `src/Negosio.Application/Staff/StaffContracts.cs`, `StaffValidators.cs`,
  `StaffService.cs`, `StaffInvitationService.cs`
- Modify: `src/Negosio.Api/Controllers/StaffController.cs`, `AuthController.cs` (preview branch name — optional)
- Test: `tests/Negosio.IntegrationTests/Branches/StaffBranchTests.cs`

**Interfaces:**
- Produces: `InviteStaffRequest(string Email, string Role, string? BranchId)`,
  `ChangeStaffRoleRequest(string Role, string? BranchId)`,
  `ChangeStaffBranchRequest(string BranchId)`,
  `StaffMemberDto` gains `Guid? BranchId, string? BranchName`,
  `IStaffService.ChangeBranchAsync(Guid userId, ChangeStaffBranchRequest, CancellationToken)`.

- [ ] **Step 1: Test-first — `StaffBranchTests`**

Spec §11.1 "StaffBranchTests" cases. Reuse Phase 4 `StaffTests` helpers (invite/accept/login).
- Invite Cashier `{ branch: bgcActive }` → accept → login → `GET /api/branches` → `[BGC]`.
- Invite Cashier `{ branch: bgcInactive }` → 400/409 `BRANCH_INACTIVE`.
- Invite Cashier `{}` (no branch) → 400 validation.
- Invite `{ role: Admin, branch: x }` → 400 (branch not allowed).
- `PUT /api/staff/{adminId}/role { role: Manager }` (no branch) → 400; `{ role: Manager, branchId: bgc }` → ok, then that user's `GET /api/branches` → `[BGC]`.
- `PUT /api/staff/{managerId}/role { role: Admin }` → ok, `BranchId` cleared (they can now see all).
- `POST /api/staff/{cashierId}/branch { branchId: mainId }` (no open session) → ok; the
  cashier's next `GET /api/branches` → `[MAIN]`.
- `POST /api/staff/{cashierId}/branch` while cashier owns an open session → 409 `STAFF_HAS_OPEN_REGISTER_SESSION`.

Run → FAIL.

- [ ] **Step 2: Contracts + validators**

`StaffContracts.cs`:
- `InviteStaffRequest` → add `string? BranchId`.
- `ChangeStaffRoleRequest` → add `string? BranchId`.
- add `ChangeStaffBranchRequest(string BranchId)`.
- `StaffMemberDto` → add `Guid? BranchId, string? BranchName` (append — update all
  construction sites in `StaffService`).
- `IStaffService` → add `Task<StaffMemberDto> ChangeBranchAsync(Guid userId, ChangeStaffBranchRequest request, CancellationToken cancellationToken = default);`

`StaffValidators.cs`:
- `InviteStaffRequestValidator`: `When(x => role is branch-scoped, () => RuleFor(x => x.BranchId).NotEmpty())`
  — parsing role is already done in the service; keep the validator light (BranchId is a
  Guid string) and enforce the role↔branch rule in the service where the role is known.
- add `ChangeStaffBranchRequestValidator` (BranchId NotEmpty + Guid-parseable).

- [ ] **Step 3: `StaffService` — invite + role + branch**

Add `IBranchAccessResolver`? No — the inviter is always Owner/Admin (`StaffManage`). Instead
use `_tenantDb` directly. Add a helper:

```csharp
private async Task<Guid> RequireActiveBranchAsync(string? branchId, CancellationToken ct)
{
    if (!Guid.TryParse(branchId, out var id))
        throw new ValidationAppException(new Dictionary<string,string[]>{ ["branchId"] = ["A branch is required for this role."] });
    var active = await _tenantDb.Branches.AnyAsync(b => b.TenantId == _currentUser.TenantId && b.Id == id && b.IsActive, ct);
    if (!active)
        throw new BusinessRuleException(ErrorCodes.BranchInactive, "That branch is inactive or does not exist.");
    return id;
}
```

- `InviteAsync`: after resolving `role`:
  - if `BranchRoles.IsAllBranch(role)` and `request.BranchId` present → `BusinessRuleException`
    ("Owners and admins aren't assigned to a branch.").
  - if branch-scoped → `branchId = await RequireActiveBranchAsync(request.BranchId, ct)`.
  - pass `branchId` (nullable) into `StaffInvitation.Create(..., branchId)`.
- `ChangeRoleAsync`: after resolving the new `role` and loading the tenant `User` +
  platform `login`:
  - transition rules (spec §7.2):
    - `!wasScoped && nowScoped` → `branchId = await RequireActiveBranchAsync(request.BranchId, ct)`; `user.ChangeRole(role); user.AssignBranch(branchId);`
    - `wasScoped && nowScoped` → `user.ChangeRole(role);` if `request.BranchId` present → `user.AssignBranch(await RequireActiveBranchAsync(...))` else keep.
    - `wasScoped && !nowScoped` → `user.ChangeRole(role); user.ClearBranch();`
  - platform `login.ChangeRole(role)` as today (platform has no branch).
- `ChangeBranchAsync`:
  ```csharp
  var user = load tenant User (404 STAFF_NOT_FOUND);
  if (BranchRoles.IsAllBranch(user.Role)) throw new BusinessRuleException(ErrorCodes.BranchForbidden, "Owners and admins aren't assigned to a branch.");
  if (await HasOpenSessionAsync(userId, ct)) throw new ConflictException(ErrorCodes.StaffHasOpenRegisterSession, "Close or force-close this person's open register session first.");
  var branchId = await RequireActiveBranchAsync(request.BranchId, ct);
  user.AssignBranch(branchId);
  await _tenantDb.SaveChangesAsync(ct);
  return projected dto;
  ```
- `ListAsync` — join `Branches` for `BranchName` on both member rows and invitation rows;
  populate `BranchId`/`BranchName`.

- [ ] **Step 4: `StaffInvitationService` — thread branch through accept**

`AcceptAsync` / `CreateTenantUserAsync` — pass `invitation.BranchId` into
`User.Create(userId, tenantId, email, firstName, lastName, role, invitation.BranchId)`.
`PreviewAsync` — optionally include `BranchName` (needs a tenant-DB lookup; the service
already opens the tenant DB factory elsewhere — acceptable, or skip for v1 and leave
`InvitationPreviewDto` unchanged).

- [ ] **Step 5: Controller route**

`StaffController` — add:
```csharp
[HttpPost("{id:guid}/branch")]
public async Task<ActionResult<StaffMemberDto>> ChangeBranch(
    Guid id, [FromBody] ChangeStaffBranchRequest request, CancellationToken ct)
    => Ok(await _staff.ChangeBranchAsync(id, request, ct));
```

- [ ] **Step 6: Run — `StaffBranchTests` + Phase 4 `StaffTests` regression + full suite**

Phase 4 `StaffTests` invite calls now need a `branchId` for Cashier/Manager invites — update
those helper calls (`InviteAsync(email, role, branchId)`).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(staff): branch on invitation, role/branch reconciliation, change-branch"
```

---

## Task 11: Frontend — POS branch flow, staff branch UI, sales filter

**Files:**
- Modify: `src/api/types.ts`, `src/api/pos.ts`, `src/api/staff.ts`, `src/lib/posStorage.ts`,
  `src/lib/roles.ts`
- Create: `src/components/pos/BranchPicker.tsx`, `src/components/staff/ChangeBranchModal.tsx`
- Modify: `src/pages/PosPage.tsx`, `src/components/pos/RegisterPicker.tsx`,
  `src/components/pos/PosShell.tsx`, `src/pages/SalesPage.tsx`, `src/pages/SaleDetailPage.tsx`,
  `src/pages/StaffPage.tsx`, `src/components/staff/InviteStaffModal.tsx`,
  `src/components/staff/ChangeRoleModal.tsx`

- [ ] **Step 1: Types + API**

`src/api/types.ts` — add `PosContextDto`, `PosRegisterDto`, `PosOpenSessionDto`; add
`branchId` to `InviteStaffRequest` / `ChangeStaffRoleRequest`; add `ChangeStaffBranchRequest`;
add `branchId`/`branchName` to `StaffMemberDto`.

`src/api/pos.ts` — add `posApi.context()` → `GET /api/pos/context`,
`posApi.registers(branchId?)` → `GET /api/pos/registers`.

`src/api/staff.ts` — add `branchId` to `invite` / `changeRole` bodies;
add `changeBranch(id, { branchId })` → `POST /api/staff/{id}/branch`.

`src/lib/posStorage.ts` — add:
```ts
readBranch: (c: { tenantId: string }) => safeGet(`negosio.pos.branch.v1.${c.tenantId}`),
writeBranch: (c: { tenantId: string }, branchId: string) =>
  safeSet(`negosio.pos.branch.v1.${c.tenantId}`, branchId),
clearBranch: (c: { tenantId: string }) => safeRemove(`negosio.pos.branch.v1.${c.tenantId}`),
```

`src/lib/roles.ts` — add `BRANCH_SCOPED_ROLES: UserRole[]` + `isBranchScoped(role)` mirroring
`BranchRoles` (Manager/Cashier/InventoryStaff/KitchenStaff/Viewer).

- [ ] **Step 2: `BranchPicker` + `RegisterPicker` rework**

`components/pos/BranchPicker.tsx` — mirror `RegisterPicker`; list active branches as buttons;
`onPick(branchId)`.

`components/pos/RegisterPicker.tsx` — take `registers: PosRegisterDto[]`. Per register:
- `openSession == null` → enabled button "Available".
- `openSession?.mine` → enabled, label "Your open session — Continue", primary style.
- `openSession && !openSession.mine` → disabled, "In use by {openSession.openedByName}".

- [ ] **Step 3: `PosPage.tsx` rework**

Replace `branchesApi.list()` + `branches[0]` with:
```tsx
const ctx = useQuery({ queryKey: ['pos','context'], queryFn: posApi.context })
```
- `ctx.data.canPickBranch === false` → `branchId = ctx.data.branchId` (or, for the
  1-active-branch owner, that one). No picker.
- `canPickBranch === true` → `branchId = override ?? posStorage.readBranch({tenantId}) (if in ctx.branches) ?? null`;
  `null` → `<BranchPicker branches={ctx.data.branches} onPick={b => { posStorage.writeBranch(...); setOverride(b) }} />`.
- Then `useQuery(['pos','registers', branchId], () => posApi.registers(branchId))` →
  `<RegisterPicker registers={...} onPick={...} onContinue={session => straight to terminal}>`.
- Keep the existing session-gate / opening-cash / terminal flow. `PosShell` gets a
  `branchName` prop (show when `canPickBranch` or `ctx.branches.length > 1`) and a
  "Switch branch" button (only when `canPickBranch`, only at the picker/gate — clears
  `posStorage.clearBranch` + register override).
- Error handling: `CASHIER_SESSION_OPEN` → callout with a "Continue your session" action
  (call `sessionsApi.current({})` → navigate to terminal); `REGISTER_SESSION_ALREADY_OPEN`
  → refetch registers; `SESSION_NOT_OWNED` → back to picker.

Keep the register-resolution derived-state discipline (no effect-seeded state).

- [ ] **Step 4: Sales + Sale detail**

`SalesPage.tsx` — add `branchesApi.list({ includeInactive: true })` query, `multiBranch =
branches.length > 1`, and a branch `Select` (like `RegistersPage`) wired to a `branchId`
filter in `usePagedQuery`. Pass `branchId` to `salesApi.list`.

`SaleDetailPage.tsx` — wrap the existing `d.sale.branchName` line in
`{multiBranch && (...)}` using the same branches query.

- [ ] **Step 5: Staff branch UI**

`InviteStaffModal.tsx` — when the selected role `isBranchScoped`, show a required Branch
`Select` (`branchesApi.list()` — active only). Include `branchId` in the mutation body.

`ChangeRoleModal.tsx` — when the target role `isBranchScoped` and the member is currently
all-branch (or no branch), show the Branch `Select` (required). Send `branchId`.

`components/staff/ChangeBranchModal.tsx` (new) — Branch `Select` (active), calls
`staffApi.changeBranch`. On 409 `STAFF_HAS_OPEN_REGISTER_SESSION` show the server message in
a `Callout`.

`StaffPage.tsx` — add a "Branch" column (`m.branchName ?? '—'`); add a "Change branch" row
action for branch-scoped members (Owner/Admin actor, not self).

- [ ] **Step 6: Lint + build**

`cd web/negosio-web && npm run lint && npm run build` → clean.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(web): POS branch flow, register availability, staff branch assignment, sales branch filter"
```

---

## Task 12: Full verification + browser walkthrough + final report

**Files:**
- Modify: `handover.md`
- Create (scratchpad, not committed): `verify-branches.mjs`

- [ ] **Step 1: Full backend suite**

Kill stale processes. Run `dotnet build Negosio.sln` (0/0) then `dotnet test Negosio.sln`.
Record the unit + integration counts + duration. Fix any remaining regressions (do **not**
weaken assertions). Expected: all green, counts up from 76 / 114.

- [ ] **Step 2: Frontend**

`cd web/negosio-web && npm run lint && npm run build` — clean. Record bundle size.

- [ ] **Step 3: Browser walkthrough**

Start the API (`ASPNETCORE_ENVIRONMENT=Development …`) + `npm run dev`. Write
`verify-branches.mjs` (headless Chrome + CDP, pattern from
`.../scratchpad/verify-staff.mjs` and the memory note `visual-verify-headless-chrome`).
Execute **every** step of spec §12 — the 23-step cashier walkthrough, the Owner multi-branch
flows, per-branch numbering, foreign-branch rejection, and the RBAC regression sweep across
`/dashboard /categories /products /inventory /inventory/movements /registers /pos /sales
/settings /staff /branches` for Owner and Cashier. Capture screenshots. Assert **zero
console errors**.

Do not claim a step passed unless the script actually executed it.

- [ ] **Step 4: Write the final report**

Rewrite `handover.md` for Phase 5 with the 9 sections from the brief:
1. Architecture 2. Backend 3. Frontend 4. Multi-Branch Correctness 5. Security 6. Tests
7. Browser Verification 8. Git State 9. Remaining Limitations (verbatim list from spec §1).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs: Phase 5 handover — branch management + branch-scoped access, ready for review"
```

- [ ] **Step 6: STOP**

Do **not** merge `feature/branch-management`. Do not start Phase 6. Report the branch state
(commits ahead of master, working tree clean, not pushed unless asked) and hand back.

---

## Self-review notes

- **Spec coverage:** every spec section maps to a task — §3 → T4/T8, §4 → T5, §5 → T7,
  §6 → T1/T2, §7 → T10, §8 → T6, §9 → T8, §10 → T3/T9/T11, §11 → tests in T1–T10, §12 → T12.
- **Migrations:** T4 (tenant `User.BranchId` + backfill, platform `StaffInvitation.BranchId`),
  T8 (tenant session-owner index). Three files, two `dotnet-ef` invocations in T4 + one in T8.
- **Ordering risk:** T6 (enforcement) and T7 (auth) both depend on T5 (resolver) and T4
  (`BranchId`). T9/T11 depend on T5/T8. T10 depends on T4/T8 (open-session helper). Order as
  written.
- **Regression hotspot:** T6 Step 8 and T7 Step 5 and T8 Step 7 and T10 Step 6 each require
  auditing existing integration tests that seed non-Owner tokens or open multiple sessions —
  budgeted explicitly in those steps.
- **No placeholders:** all new types have full signatures; analogous CRUD/modal boilerplate
  points at the specific existing file to mirror (`RegisterService`, `RegisterFormModal`,
  `StaffPage`, `verify-staff.mjs`).
