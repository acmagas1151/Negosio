# Phase 6 — Void Sales & Cash Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add full-sale voiding (with a Cashier direct/manager-approval authorization split and full audit trail), a per-user `SalesVoid` permission grant, register cash-in/cash-out movements, and reconciliation that accounts for both.

**Architecture:** Extend the existing `Sale` aggregate with a `Void()` domain method reusing its already-reserved `VoidedAtUtc`/`VoidReason` columns; add two small new tenant-DB entities (`UserPermissionGrant`, `RegisterCashMovement`); add one new application service per concern (`SalesVoidPermissionService`, `RegisterCashMovementService`, `ApproverVerificationService`, `VoidSaleService`) following the existing `ReturnService`/`CheckoutService` transaction-and-branch-guard patterns; extend `RegisterSessionService.ReconcileAndCloseAsync`'s live reconciliation query with two new terms. Frontend adds one modal (void, with two sub-variants), one Staff permission toggle, two POS session-control modals (Cash In/Out), and extends `CloseSessionModal`'s breakdown.

**Tech Stack:** .NET 9 / EF Core 9 / SQL Server (tenant DB), FluentValidation, xUnit + FluentAssertions + `WebApplicationFactory`; React 19 / TypeScript strict / TanStack Query v5 / Tailwind v4, reusing `Modal`/`Button`/`TextField`/`Callout`/`useToast`/`useCan`.

**Spec:** `docs/superpowers/specs/2026-09-02-void-cash-operations-design.md` — this plan implements every section of that spec; read it alongside this plan (error-code names, the authorization matrix, and the cash formula are defined there and only summarized here).

## Global Constraints

- Void is full-sale only, keeps the original `SaleNumber` — never allocate a new document number for a void.
- Eligibility is checked in exactly this order, for every actor including Owner: `Status == Completed` → no Returns → session still Open → `Sale.CompletedAtUtc.Date == nowUtc.Date` (via injected `TimeProvider`, never `DateTime.UtcNow` directly in new eligibility code).
- Authorization: Owner/Admin direct any branch; Manager direct own branch only; Cashier direct only with a `SalesVoid` grant, otherwise one-time Manager/Admin/Owner password reverification; InventoryStaff/Viewer/KitchenStaff never see the action.
- `UserPermissionGrant` row existence = the permission is active. Revoke deletes the row. No grant/revoke history. Unique `(TenantId, UserId, Permission)`.
- Approver reverification reuses `IPasswordHasher` + `IPlatformDbContext` exactly as `AuthService.LoginAsync` does (timing-equalized hash for unknown emails) but never issues a JWT, never logs/persists the password, and authenticates exactly one void request (never cached/reusable).
- `StockMovementType` values are additive only — never renumber 1-9; new value `SaleVoid = 10`.
- Cash formula: `Expected = Opening + GrossCashSales − VoidedCashSales − RefundCashOut + CashIn − CashOut`. Void is only ever reachable while its session is open, so a closed `RegisterSession`'s `ExpectedCash`/`ClosingCash`/`CashDifference` are never recomputed or touched by this phase.
- `RegisterCashMovement` amount is always `> 0`; direction comes from `Type`, never a signed amount. Creatable only by the session's own owner (`session.OpenedByUserId == currentUser`), on an Open session.
- No line-level/partial void, no Reporting, no stock transfers, no purchasing, no F&B, no multi-currency, no tenant timezone, no closed-session cash correction of any kind — do not build any of these even incidentally.
- Branch-scoped users get 404 (not 403) on a sale/session outside their branch, matching `ReturnService.GuardSaleBranchAsync`'s existing pattern — do not switch this to 403.
- **Concurrency (required, not optional):** the void eligibility checks must be re-verified inside the same transaction as inventory reversal + `Sale.Void()` + `SaveChanges`. `Sale` gets a `RowVersion` concurrency token (protects double-void and void-vs-return — both `VoidSaleService` and `ReturnService` must catch `DbUpdateConcurrencyException`, re-fetch, and throw the sale's *accurate current-state* error, not a generic conflict). Void-vs-register-session-close is a cross-entity race `RowVersion` cannot catch (Void never writes to `RegisterSessions`) — both `VoidSaleService.VoidAsync` and `RegisterSessionService.ReconcileAndCloseAsync` must take a pessimistic `WITH (UPDLOCK, HOLDLOCK)` read-lock on the session row as the very first statement inside their transaction, before any other read, to fully serialize the two operations against each other.
- Capture exactly one `nowUtc` (from `TimeProvider`) per void attempt and reuse it for both the same-UTC-day eligibility check and `Sale.VoidedAtUtc` — `Sale.Void(...)` takes `nowUtc` as a parameter, it never calls `DateTime.UtcNow` itself.
- `VoidSaleService` must explicitly check the actor's role is one of exactly Owner/Admin/Manager/Cashier and reject anything else with a generic forbidden error — never rely solely on the controller's `SalesView` policy, and never treat "not Owner/Admin/Manager" as an implicit synonym for "is Cashier."
- A non-all-branch Staff-list caller (Manager) sees only rows where `BranchId` equals their own assigned branch — **not** `BranchId == assigned || BranchId == null`. A null `BranchId` always means Owner/Admin; a Manager must never see those rows.
- Do not weaken tests, lint, TypeScript, or authorization to make something pass — every fix must be a real fix.
- **Do NOT merge `feature/void-cash-operations` into `master`** at the end of this plan — stop after full verification and the final report.

---

## Task B1: Sale domain — `Void()`, audit columns, migration

**Files:**
- Modify: `src/Negosio.Domain/Entities/Sales/Sale.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs`
- Create: EF migration under `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/`
- Test: `tests/Negosio.UnitTests/Sales/SaleVoidTests.cs`

**Interfaces:**
- Produces: `Sale.Void(Guid voidedByUserId, string reason, Guid? approvedByUserId, DateTime nowUtc)`, `Sale.VoidedByUserId` (`Guid?`), `Sale.ApprovedByUserId` (`Guid?`), `Sale.RowVersion` (`byte[]`, EF concurrency token). Later tasks (B4) call `sale.Void(...)` after their own eligibility checks pass, passing one `TimeProvider`-sourced `nowUtc` they've already captured for the same-day eligibility comparison — this method's own status guard is defense in depth (throws `InvalidOperationException`, not an `AppException` — the service layer is what turns eligibility failures into typed errors). `RowVersion` requires no application code to set — SQL Server auto-increments it on every UPDATE to the row; EF just needs it mapped `IsRowVersion()` so every tracked write to a `Sale` (from `VoidSaleService` *and* the existing `ReturnService`) automatically gets an optimistic-concurrency check, without either service's code changing beyond catching the resulting exception (B4 does this for `VoidSaleService`; B4 also adds the matching catch to `ReturnService`, since it's the other writer that needs to react correctly to losing a race against a void).

- [ ] **Step 1: Write the failing unit tests**

Create `tests/Negosio.UnitTests/Sales/SaleVoidTests.cs`:

```csharp
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Sales;

public class SaleVoidTests
{
    private static Sale CompletedSale()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());
        sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 2m, DiscountType.None, 0m, 150m, 0m, 0m, 150m, 40m);
        sale.Complete(150m, 0m, 0m, 150m, 150m, 0m);
        return sale;
    }

    [Fact]
    public void Void_transitions_completed_to_voided_and_stamps_audit_fields()
    {
        var sale = CompletedSale();
        var voidedBy = Guid.NewGuid();
        var approvedBy = Guid.NewGuid();
        var nowUtc = new DateTime(2026, 9, 2, 10, 30, 0, DateTimeKind.Utc);

        sale.Void(voidedBy, "Wrong payment method", approvedBy, nowUtc);

        sale.Status.Should().Be(SaleStatus.Voided);
        sale.VoidedByUserId.Should().Be(voidedBy);
        sale.ApprovedByUserId.Should().Be(approvedBy);
        sale.VoidReason.Should().Be("Wrong payment method");
        sale.VoidedAtUtc.Should().Be(nowUtc);
    }

    [Fact]
    public void Void_uses_the_passed_timestamp_not_wall_clock()
    {
        var sale = CompletedSale();
        var farFuture = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        sale.Void(Guid.NewGuid(), "Timestamp check", null, farFuture);

        sale.VoidedAtUtc.Should().Be(farFuture); // proves Void() never calls DateTime.UtcNow itself
    }

    [Fact]
    public void Void_direct_leaves_approver_null()
    {
        var sale = CompletedSale();

        sale.Void(Guid.NewGuid(), "Duplicate transaction", approvedByUserId: null, DateTime.UtcNow);

        sale.ApprovedByUserId.Should().BeNull();
    }

    [Fact]
    public void Void_twice_throws()
    {
        var sale = CompletedSale();
        sale.Void(Guid.NewGuid(), "Wrong payment method", null, DateTime.UtcNow);

        var act = () => sale.Void(Guid.NewGuid(), "Second attempt", null, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Void_requires_a_non_blank_reason()
    {
        var sale = CompletedSale();

        var act = () => sale.Void(Guid.NewGuid(), "   ", null, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Void_before_completion_throws()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());

        var act = () => sale.Void(Guid.NewGuid(), "Never completed", null, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Negosio.UnitTests --filter SaleVoidTests`
Expected: build error — `Sale` has no `Void` method, no `VoidedByUserId`/`ApprovedByUserId` properties.

- [ ] **Step 3: Implement `Sale.Void()` and the new properties**

In `src/Negosio.Domain/Entities/Sales/Sale.cs`, add three properties next to the existing `VoidedAtUtc`/`VoidReason` (after line 68):

```csharp
public Guid? VoidedByUserId { get; private set; }

public Guid? ApprovedByUserId { get; private set; }

/// <summary>SQL Server `rowversion` — EF-managed optimistic concurrency token, never set by
/// application code. Protects any two competing writes to this row (double-void, void racing a
/// concurrent Return) — see the plan's Global Constraints and Task B4.</summary>
public byte[] RowVersion { get; private set; } = Array.Empty<byte>();
```

Add a new method after `MarkReturned()`:

```csharp
/// <summary>
/// Reverses a completed sale in full, keeping the original <see cref="SaleNumber"/>. The caller
/// (<c>VoidSaleService</c>) is responsible for the full eligibility check (status, returns,
/// session-open, same-day cutoff) and for restoring inventory in the same transaction — this
/// guard is defense in depth, re-asserting the one invariant the domain itself must never allow.
/// <paramref name="nowUtc"/> is required, not read from the clock here — the caller captures one
/// TimeProvider-sourced timestamp per void attempt and reuses it for both the same-day eligibility
/// check and this stamp, so the two can never disagree.
/// </summary>
public void Void(Guid voidedByUserId, string reason, Guid? approvedByUserId, DateTime nowUtc)
{
    if (Status != SaleStatus.Completed)
    {
        throw new InvalidOperationException("Only a completed sale can be voided.");
    }

    if (string.IsNullOrWhiteSpace(reason))
    {
        throw new ArgumentException("A void reason is required.", nameof(reason));
    }

    Status = SaleStatus.Voided;
    VoidedAtUtc = nowUtc;
    VoidReason = reason.Trim();
    VoidedByUserId = voidedByUserId;
    ApprovedByUserId = approvedByUserId;
    Touch();
}
```

- [ ] **Step 4: Run to verify the unit tests pass**

Run: `dotnet test tests/Negosio.UnitTests --filter SaleVoidTests`
Expected: 6 passed.

- [ ] **Step 5: Map the new columns and generate the migration**

In `src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs`, after the existing `builder.Property(s => s.VoidReason).HasMaxLength(500);` line, add:

```csharp
builder.Property(s => s.VoidedByUserId);
builder.Property(s => s.ApprovedByUserId);
builder.Property(s => s.RowVersion).IsRowVersion();
```

Kill any stale `Negosio.Api`/vite process first (repo convention — LocalDB file locks), then run:

```bash
dotnet dotnet-ef migrations add AddSaleVoidAudit --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
```

(Use this exact namespace/folder — it matches the actual convention on disk, e.g. `20260901092904_SessionOwnerOpenIndex.cs`; the README's `...Migrations.TenantDb` suffix is stale.)

- [ ] **Step 6: Verify the migration applies cleanly**

Run: `dotnet build Negosio.sln`
Expected: 0 errors, 0 warnings. The migration itself is exercised when the API next starts (`Database__MigrateOnStartup=true`) or by the integration test host in later tasks — no standalone apply step is needed here.

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Domain/Entities/Sales/Sale.cs \
        src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs \
        src/Negosio.Infrastructure/Persistence/Migrations/Tenant/ \
        tests/Negosio.UnitTests/Sales/SaleVoidTests.cs
git commit -m "feat(sales): add void state and audit model"
```

---

## Task B2: `UserPermissionGrant` + `SalesVoidPermissionService` + Staff visibility/permission endpoint

**Files:**
- Create: `src/Negosio.Domain/Enums/UserPermission.cs`
- Create: `src/Negosio.Domain/Entities/UserPermissionGrant.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/UserPermissionGrantConfiguration.cs`
- Create: EF migration under `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/`
- Modify: `src/Negosio.Application/Abstractions/ITenantDbContext.cs` (add `DbSet<UserPermissionGrant> UserPermissionGrants`)
- Modify: `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs` (same)
- Create: `src/Negosio.Application/Staff/SalesVoidPermissionService.cs`
- Modify: `src/Negosio.Application/Staff/StaffContracts.cs` (add `SalesVoid` to `StaffMemberDto`, new request/interface)
- Modify: `src/Negosio.Application/Staff/StaffService.cs` (`ListAsync`/`GetAsync` branch-filter for non-all-branch callers, populate `SalesVoid`)
- Modify: `src/Negosio.Api/Authorization/AuthorizationPolicies.cs` (add `StaffView`, `StaffPermissionManage`)
- Modify: `src/Negosio.Api/Controllers/StaffController.cs` (override policy on `List`/`Get`, add `PUT /api/staff/{id}/permissions`)
- Modify: `src/Negosio.Application/Common/ErrorCodes.cs`
- Test: `tests/Negosio.IntegrationTests/Sales/SalesVoidPermissionTests.cs`

**Interfaces:**
- Consumes: `IBranchAccessResolver.AssignedBranchIdAsync(CancellationToken)` (B-nothing — already exists, `src/Negosio.Application/Branches/BranchAccessResolver.cs`), `ICurrentUser` (`UserId`, `TenantId`, `Role`), `BranchRoles.IsAllBranch(UserRole)`.
- Produces: `ISalesVoidPermissionService.GrantAsync(Guid targetUserId, CancellationToken)`, `.RevokeAsync(Guid targetUserId, CancellationToken)`, `.HasGrantAsync(Guid userId, CancellationToken) -> Task<bool>` — **Task B4 (`VoidSaleService`) consumes `HasGrantAsync` directly** to decide the Cashier direct-vs-approval branch. `StaffMemberDto` gains `bool SalesVoid` (true only for a Cashier row with an active grant; always `false` for non-Cashier rows).

- [ ] **Step 1: Write the failing integration tests**

Create `tests/Negosio.IntegrationTests/Sales/SalesVoidPermissionTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class SalesVoidPermissionTests : IntegrationTest
{
    public SalesVoidPermissionTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private static HttpRequestMessage PermissionsRequest(Guid userId, bool salesVoid) =>
        new(HttpMethod.Put, $"/api/staff/{userId}/permissions") { Content = JsonContent.Create(new ChangeStaffPermissionsRequest(salesVoid)) };

    [Fact]
    public async Task Owner_grants_and_revokes_for_any_cashier()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        (await Client.SendAsync(PermissionsRequest(cashierId, true))).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        list!.Single(m => m.Id == cashierId).SalesVoid.Should().BeTrue();

        (await Client.SendAsync(PermissionsRequest(cashierId, false))).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterRevoke = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);
        afterRevoke!.Single(m => m.Id == cashierId).SalesVoid.Should().BeFalse();
    }

    [Fact]
    public async Task Manager_grants_only_within_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);

        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, bgc.Id);
        var managerId = await GetUserIdFromTokenAsync(managerToken);
        var bgcCashierToken = await AddTenantUserTokenAsync("bgc.cara@example.com", UserRole.Cashier, bgc.Id);
        var bgcCashierId = await GetUserIdFromTokenAsync(bgcCashierToken);
        var mainCashierToken = await AddTenantUserTokenAsync("main.cara@example.com", UserRole.Cashier, main);
        var mainCashierId = await GetUserIdFromTokenAsync(mainCashierToken);

        Authorize(managerToken);

        (await Client.SendAsync(PermissionsRequest(bgcCashierId, true))).StatusCode.Should().Be(HttpStatusCode.OK);

        var forbidden = await Client.SendAsync(PermissionsRequest(mainCashierId, true));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await forbidden.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.BranchForbidden);
    }

    [Fact]
    public async Task Cashier_cannot_grant_their_own_permission()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        Authorize(cashierToken);
        var res = await Client.SendAsync(PermissionsRequest(cashierId, true));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_reaches_staff_list_scoped_to_their_own_branch_only()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);
        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, bgc.Id);
        await AddTenantUserTokenAsync("main.cara@example.com", UserRole.Cashier, main);

        Authorize(managerToken);
        var list = await Client.GetFromJsonAsync<List<StaffMemberDto>>("/api/staff", TestJson.Options);

        // Strictly BGC only — not Owner/Admin (whose BranchId is null) and not MAIN.
        list!.Should().OnlyContain(m => m.BranchId == bgc.Id);
        list!.Should().NotContain(m => m.Role is UserRole.Owner or UserRole.Admin);
    }

    [Fact]
    public async Task Granting_a_permission_to_a_non_cashier_role_is_rejected()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, branchId);
        var managerId = await GetUserIdFromTokenAsync(managerToken);

        var res = await Client.SendAsync(PermissionsRequest(managerId, true));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

This test file assumes a `GetUserIdFromTokenAsync(string token)` helper on the shared `IntegrationTest` base. If it does not already exist, add it there (find the base class — `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs` — alongside `AddTenantUserTokenAsync`) as:

```csharp
protected async Task<Guid> GetUserIdFromTokenAsync(string token)
{
    Authorize(token);
    var me = await Client.GetFromJsonAsync<AuthUserDto>("/api/auth/me", TestJson.Options);
    return me!.Id;
}
```

(Check the actual `/api/auth/me`-equivalent route and `AuthUserDto` field name for the current user's id before adding — reuse whatever `AddTenantUserTokenAsync` itself already calls to mint the token, since it necessarily knows the new user's id already; prefer changing `AddTenantUserTokenAsync` to return `(string Token, Guid UserId)` and updating every existing call site over adding a second HTTP round trip, if that's a smaller diff.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Negosio.IntegrationTests --filter SalesVoidPermissionTests`
Expected: compile errors — `ChangeStaffPermissionsRequest`, `StaffMemberDto.SalesVoid` don't exist yet, `/api/staff/{id}/permissions` 404s.

- [ ] **Step 3: Add the domain entity**

Create `src/Negosio.Domain/Enums/UserPermission.cs`:

```csharp
namespace Negosio.Domain.Enums;

/// <summary>
/// A per-user operational permission override, layered on top of role-based access. Persisted
/// numerically; values must stay stable. Phase 6 introduces exactly one value — this is not a
/// general permissions matrix, and should not grow without a fresh design discussion.
/// </summary>
public enum UserPermission
{
    SalesVoid = 1
}
```

Create `src/Negosio.Domain/Entities/UserPermissionGrant.cs`:

```csharp
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A per-user permission override. Row existence is the sole source of truth — there is no
/// soft-revoke or grant/revoke history; the void's own audit fields already record who acted.
/// A unique (TenantId, UserId, Permission) index prevents duplicates.
/// </summary>
public class UserPermissionGrant : Entity
{
    private UserPermissionGrant()
    {
    }

    private UserPermissionGrant(Guid tenantId, Guid userId, UserPermission permission, Guid grantedByUserId)
    {
        TenantId = tenantId;
        UserId = userId;
        Permission = permission;
        GrantedByUserId = grantedByUserId;
        GrantedAtUtc = DateTime.UtcNow;
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public UserPermission Permission { get; private set; }

    public Guid GrantedByUserId { get; private set; }

    public DateTime GrantedAtUtc { get; private set; }

    public static UserPermissionGrant Grant(Guid tenantId, Guid userId, UserPermission permission, Guid grantedByUserId) =>
        new(tenantId, userId, permission, grantedByUserId);
}
```

- [ ] **Step 4: Map, register the DbSet, and generate the migration**

Create `src/Negosio.Infrastructure/Persistence/Configurations/UserPermissionGrantConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class UserPermissionGrantConfiguration : IEntityTypeConfiguration<UserPermissionGrant>
{
    public void Configure(EntityTypeBuilder<UserPermissionGrant> builder)
    {
        builder.ToTable("UserPermissionGrants");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.TenantId).IsRequired();
        builder.Property(g => g.UserId).IsRequired();
        builder.Property(g => g.Permission).IsRequired().HasConversion<int>();
        builder.Property(g => g.GrantedByUserId).IsRequired();
        builder.Property(g => g.CreatedAtUtc).IsRequired();
        builder.Property(g => g.UpdatedAtUtc).IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => new { g.TenantId, g.UserId, g.Permission })
            .IsUnique().HasDatabaseName("IX_UserPermissionGrants_TenantId_UserId_Permission");
    }
}
```

Add to both `ITenantDbContext` (in `src/Negosio.Application/Abstractions/ITenantDbContext.cs` — locate it via `grep -rn "DbSet<Sale>" src/Negosio.Application/Abstractions/`) and `TenantDbContext` (`src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs:` after the `RefundPayments` DbSet):

```csharp
public DbSet<UserPermissionGrant> UserPermissionGrants => Set<UserPermissionGrant>();
```

Generate the migration:

```bash
dotnet dotnet-ef migrations add AddUserPermissionGrants --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
```

- [ ] **Step 5: Add error codes**

In `src/Negosio.Application/Common/ErrorCodes.cs`, append a new block after the Phase 5 block:

```csharp
// ---- Phase 6: Void sales & cash operations ----
public const string SaleNotVoidable = "SALE_NOT_VOIDABLE";
public const string SaleHasReturns = "SALE_HAS_RETURNS";
public const string VoidSessionClosed = "VOID_SESSION_CLOSED";
public const string VoidCutoffExpired = "VOID_CUTOFF_EXPIRED";
public const string VoidApprovalRequired = "VOID_APPROVAL_REQUIRED";
public const string InvalidApproverCredentials = "INVALID_APPROVER_CREDENTIALS";
public const string VoidApproverNotAuthorized = "VOID_APPROVER_NOT_AUTHORIZED";
public const string VoidApproverWrongBranch = "VOID_APPROVER_WRONG_BRANCH";
public const string SalesVoidSelfGrant = "SALES_VOID_SELF_GRANT";
public const string SalesVoidGrantRoleInvalid = "SALES_VOID_GRANT_ROLE_INVALID";
public const string CashMovementInvalidAmount = "CASH_MOVEMENT_INVALID_AMOUNT";
public const string CashMovementSessionClosed = "CASH_MOVEMENT_SESSION_CLOSED";
public const string CashMovementNotOwner = "CASH_MOVEMENT_NOT_OWNER";
```

(This task only needs `SalesVoidSelfGrant`/`SalesVoidGrantRoleInvalid`/`BranchForbidden` (already exists) — the rest are added here so later tasks (B3, B4) don't need to re-touch this file's Phase 6 block header.)

- [ ] **Step 6: Implement `SalesVoidPermissionService`**

Add to `src/Negosio.Application/Staff/StaffContracts.cs` (append near `StaffMemberDto`):

```csharp
public sealed record ChangeStaffPermissionsRequest(bool SalesVoid);

public interface ISalesVoidPermissionService
{
    Task<bool> HasGrantAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<StaffMemberDto> SetAsync(Guid targetUserId, bool salesVoid, CancellationToken cancellationToken = default);
}
```

Modify `StaffMemberDto`'s record definition to add one more trailing field: `bool SalesVoid` (append after `BranchName`) — update every existing construction site in `StaffService.cs` (`ListAsync`'s two `Select`/`Concat` builders and `ToDtoAsync`) to pass `SalesVoid: false` for now (Step 8 below wires the real value into `ListAsync`/`ToDtoAsync` via a batch lookup, not per-row, to avoid N+1 queries).

Create `src/Negosio.Application/Staff/SalesVoidPermissionService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Staff;

public sealed class SalesVoidPermissionService : ISalesVoidPermissionService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;

    public SalesVoidPermissionService(ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
    }

    public async Task<bool> HasGrantAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;
        return await _db.UserPermissionGrants.AsNoTracking()
            .AnyAsync(g => g.TenantId == tenantId && g.UserId == userId && g.Permission == UserPermission.SalesVoid, cancellationToken);
    }

    public async Task<StaffMemberDto> SetAsync(Guid targetUserId, bool salesVoid, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        if (targetUserId == _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.SalesVoidSelfGrant, "You cannot change your own void permission.");
        }

        var target = await _db.Users.SingleOrDefaultAsync(u => u.TenantId == tenantId && u.Id == targetUserId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");

        if (target.Role != UserRole.Cashier)
        {
            throw new BusinessRuleException(ErrorCodes.SalesVoidGrantRoleInvalid, "Only a Cashier can hold this permission.");
        }

        if (!BranchRoles.IsAllBranch(_currentUser.Role))
        {
            var assigned = (await _branchAccess.AssignedBranchIdAsync(cancellationToken))!.Value;
            if (target.BranchId != assigned)
            {
                throw new ForbiddenAppException(ErrorCodes.BranchForbidden, "You can only manage this permission for staff in your own branch.");
            }
        }

        var existing = await _db.UserPermissionGrants
            .SingleOrDefaultAsync(g => g.TenantId == tenantId && g.UserId == targetUserId && g.Permission == UserPermission.SalesVoid, cancellationToken);

        if (salesVoid && existing is null)
        {
            _db.UserPermissionGrants.Add(UserPermissionGrant.Grant(tenantId, targetUserId, UserPermission.SalesVoid, _currentUser.UserId));
        }
        else if (!salesVoid && existing is not null)
        {
            _db.UserPermissionGrants.Remove(existing);
        }

        await _db.SaveChangesAsync(cancellationToken);

        string? branchName = target.BranchId is { } bid
            ? await _db.Branches.AsNoTracking().Where(b => b.Id == bid).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new StaffMemberDto(
            target.Id, StaffMemberKind.Member, target.FirstName, target.LastName, target.Email, target.Role,
            target.IsActive ? StaffMemberStatus.Active : StaffMemberStatus.Deactivated,
            JoinedAtUtc: target.CreatedAtUtc, InvitedAtUtc: null, ExpiresAtUtc: null, InvitedByName: null,
            BranchId: target.BranchId, BranchName: branchName, SalesVoid: salesVoid);
    }
}
```

Register it in DI alongside the other `Staff` services (find where `IStaffService` is registered, e.g. `src/Negosio.Application/DependencyInjection.cs` or `Program.cs`, and add `services.AddScoped<ISalesVoidPermissionService, SalesVoidPermissionService>();` next to it).

- [ ] **Step 7: Wire `StaffService.ListAsync`/`GetAsync`/`ToDtoAsync` to populate `SalesVoid` and branch-filter for Manager**

In `src/Negosio.Application/Staff/StaffService.cs`, inject `IBranchAccessResolver branchAccess` (constructor param, add to the DI-resolved list). In `ListAsync`, after loading `users`, batch-load the grant set once (avoids N+1):

```csharp
var grantedUserIds = (await _tenant.UserPermissionGrants.AsNoTracking()
    .Where(g => g.TenantId == tenantId && g.Permission == UserPermission.SalesVoid)
    .Select(g => g.UserId)
    .ToListAsync(cancellationToken)).ToHashSet();
```

Pass `SalesVoid: grantedUserIds.Contains(u.Id) && role == UserRole.Cashier` into each `StaffMemberDto` built for a member row (invitation rows always pass `SalesVoid: false`). Then, right before the final `return members.Concat(invitationRows)...`, branch-filter for a non-all-branch caller:

```csharp
var assigned = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
if (assigned is { } branchId)
{
    // Strictly BranchId == branchId — NOT `|| BranchId == null`. A null BranchId always means
    // Owner/Admin (Phase 5 invariant); a Manager must never see those rows, so they're excluded
    // outright rather than treated as "visible to everyone."
    return members.Where(m => m.BranchId == branchId)
        .Concat(invitationRows.Where(i => i.BranchId == branchId))
        .OrderBy(m => m.Kind == StaffMemberKind.Invitation)
        .ThenBy(m => (m.FirstName + m.LastName + m.Email).ToLowerInvariant())
        .ToList();
}
```

(keep the existing unfiltered `OrderBy`/`ToList()` tail for the Owner/Admin, all-branch path). Update `ToDtoAsync` similarly to look up the grant (single `AnyAsync` call, called once per response — acceptable, it's a single-row read after a write) and pass the real `SalesVoid` value instead of `false`.

- [ ] **Step 8: Add the policies and endpoints**

In `src/Negosio.Api/Authorization/AuthorizationPolicies.cs`, add two new constants near `StaffManage` and register them in `AddNegosioPolicies`:

```csharp
/// <summary>Read the staff roster — Owner/Admin see the whole tenant, Manager sees only their branch (service-enforced).</summary>
public const string StaffView = "StaffView";

/// <summary>Grant/revoke the SalesVoid permission — Owner/Admin any Cashier, Manager only their own branch's Cashiers (service-enforced).</summary>
public const string StaffPermissionManage = "StaffPermissionManage";
```

```csharp
options.AddPolicy(StaffView, policy =>
    policy.RequireAuthenticatedUser()
          .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));

options.AddPolicy(StaffPermissionManage, policy =>
    policy.RequireAuthenticatedUser()
          .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));
```

In `src/Negosio.Api/Controllers/StaffController.cs`: add `[Authorize(Policy = AuthorizationPolicies.StaffView)]` on `List` and `Get` (overriding the class-level `StaffManage`), inject `ISalesVoidPermissionService`, and add:

```csharp
[HttpPut("{id:guid}/permissions")]
[Authorize(Policy = AuthorizationPolicies.StaffPermissionManage)]
[ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
public async Task<ActionResult<StaffMemberDto>> SetPermissions(
    Guid id,
    [FromBody] ChangeStaffPermissionsRequest request,
    CancellationToken cancellationToken)
    => Ok(await _permissions.SetAsync(id, request.SalesVoid, cancellationToken));
```

(constructor takes `ISalesVoidPermissionService permissions` alongside the existing `IStaffService staff`.)

- [ ] **Step 9: Run to verify the tests pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter SalesVoidPermissionTests`
Expected: 5 passed.

- [ ] **Step 10: Commit**

```bash
git add src/Negosio.Domain/Enums/UserPermission.cs \
        src/Negosio.Domain/Entities/UserPermissionGrant.cs \
        src/Negosio.Infrastructure/Persistence/Configurations/UserPermissionGrantConfiguration.cs \
        src/Negosio.Infrastructure/Persistence/Migrations/Tenant/ \
        src/Negosio.Application/Abstractions/ITenantDbContext.cs \
        src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs \
        src/Negosio.Application/Staff/ \
        src/Negosio.Api/Authorization/AuthorizationPolicies.cs \
        src/Negosio.Api/Controllers/StaffController.cs \
        src/Negosio.Application/Common/ErrorCodes.cs \
        tests/Negosio.IntegrationTests/Sales/SalesVoidPermissionTests.cs
git commit -m "feat(auth): add cashier SalesVoid permission grants"
```

---

## Task B3: `RegisterCashMovement` entity, service, and endpoints

**Files:**
- Create: `src/Negosio.Domain/Enums/CashMovementType.cs`
- Create: `src/Negosio.Domain/Entities/Pos/RegisterCashMovement.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/RegisterCashMovementConfiguration.cs`
- Create: EF migration under `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/`
- Modify: `src/Negosio.Application/Abstractions/ITenantDbContext.cs`, `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs` (add `DbSet<RegisterCashMovement>`)
- Modify: `src/Negosio.Application/Registers/RegisterContracts.cs` (new DTOs/requests/interface)
- Create: `src/Negosio.Application/Registers/RegisterCashMovementService.cs`
- Create: `src/Negosio.Application/Registers/RegisterCashMovementValidator.cs`
- Modify: `src/Negosio.Api/Controllers/RegisterSessionsController.cs` (two new endpoints)
- Test: `tests/Negosio.IntegrationTests/Registers/RegisterCashMovementTests.cs`

**Interfaces:**
- Consumes: `ICurrentUser`, `IBranchAccessResolver` (for the list endpoint's Manager/Owner/Admin view scoping).
- Produces: `IRegisterCashMovementService.CreateAsync(Guid sessionId, CreateCashMovementRequest, CancellationToken) -> Task<RegisterCashMovementDto>`, `.ListAsync(Guid sessionId, CancellationToken) -> Task<IReadOnlyList<RegisterCashMovementDto>>`. **Task B5 (reconciliation) consumes `_db.RegisterCashMovements` directly** (not through this service) inside `ReconcileAndCloseAsync`'s query — this service is for the two new HTTP endpoints only.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/Negosio.IntegrationTests/Registers/RegisterCashMovementTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

public class RegisterCashMovementTests : IntegrationTest
{
    public RegisterCashMovementTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Cashier_can_cash_in_and_out_on_their_own_open_session()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);

        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 1000m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var cashIn = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 500m, "Additional float"));
        cashIn.StatusCode.Should().Be(HttpStatusCode.Created);

        var cashOut = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashOut, 200m, "Petty cash"));
        cashOut.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await Client.GetFromJsonAsync<List<RegisterCashMovementDto>>(
            $"/api/register-sessions/{session.Id}/cash-movements", TestJson.Options);
        list!.Should().HaveCount(2);
    }

    [Fact]
    public async Task Amount_must_be_greater_than_zero()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);

        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 0m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var res = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 0m, "Bad amount"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cannot_create_a_movement_on_another_users_session()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierAToken = await AddTenantUserTokenAsync("a@example.com", UserRole.Cashier, branchId);
        var cashierBToken = await AddTenantUserTokenAsync("b@example.com", UserRole.Cashier, branchId);

        Authorize(cashierAToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 500m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        Authorize(cashierBToken);
        var res = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 100m, "Not mine"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.CashMovementNotOwner);
    }

    [Fact]
    public async Task Cannot_create_a_movement_on_a_closed_session()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);

        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 500m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
        await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/close", new CloseRegisterSessionRequest(500m));

        var res = await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 100m, "Too late"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.CashMovementSessionClosed);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Negosio.IntegrationTests --filter RegisterCashMovementTests`
Expected: compile errors — none of the new types/endpoints exist yet.

- [ ] **Step 3: Add the domain entity**

Create `src/Negosio.Domain/Enums/CashMovementType.cs`:

```csharp
namespace Negosio.Domain.Enums;

/// <summary>Direction of a register cash movement. Persisted numerically; values must stay stable.</summary>
public enum CashMovementType
{
    CashIn = 1,
    CashOut = 2
}
```

Create `src/Negosio.Domain/Entities/Pos/RegisterCashMovement.cs`:

```csharp
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A manual cash addition or removal against an open register session — float top-ups, petty
/// cash, bank deposits. Append-only, like <see cref="StockMovement"/>. Amount is always positive;
/// direction comes from <see cref="Type"/>, never a signed amount.
/// </summary>
public class RegisterCashMovement : Entity
{
    private RegisterCashMovement()
    {
    }

    private RegisterCashMovement(
        Guid tenantId, Guid branchId, Guid registerSessionId, CashMovementType type,
        decimal amount, string reason, Guid createdByUserId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        RegisterSessionId = registerSessionId;
        Type = type;
        Amount = amount;
        Reason = reason;
        CreatedByUserId = createdByUserId;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid RegisterSessionId { get; private set; }

    public CashMovementType Type { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid CreatedByUserId { get; private set; }

    public static RegisterCashMovement Create(
        Guid tenantId, Guid branchId, Guid registerSessionId, CashMovementType type,
        decimal amount, string reason, Guid createdByUserId)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        return new RegisterCashMovement(tenantId, branchId, registerSessionId, type, amount, reason.Trim(), createdByUserId);
    }
}
```

- [ ] **Step 4: Map, register the DbSet, and generate the migration**

Create `src/Negosio.Infrastructure/Persistence/Configurations/RegisterCashMovementConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RegisterCashMovementConfiguration : IEntityTypeConfiguration<RegisterCashMovement>
{
    public void Configure(EntityTypeBuilder<RegisterCashMovement> builder)
    {
        builder.ToTable("RegisterCashMovements");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.BranchId).IsRequired();
        builder.Property(m => m.RegisterSessionId).IsRequired();
        builder.Property(m => m.Type).IsRequired().HasConversion<int>();
        builder.Property(m => m.Amount).HasPrecision(18, 2);
        builder.Property(m => m.Reason).IsRequired().HasMaxLength(500);
        builder.Property(m => m.CreatedByUserId).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc).IsRequired();

        builder.HasOne<RegisterSession>().WithMany().HasForeignKey(m => m.RegisterSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.TenantId, m.RegisterSessionId, m.CreatedAtUtc })
            .HasDatabaseName("IX_RegisterCashMovements_TenantId_RegisterSessionId_CreatedAtUtc");
    }
}
```

Add `public DbSet<RegisterCashMovement> RegisterCashMovements => Set<RegisterCashMovement>();` to `ITenantDbContext` and `TenantDbContext` (after `RegisterSessions`). Generate the migration:

```bash
dotnet dotnet-ef migrations add AddRegisterCashMovements --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
```

- [ ] **Step 5: Add the contracts, service, and validator**

In `src/Negosio.Application/Registers/RegisterContracts.cs`, add:

```csharp
public sealed record RegisterCashMovementDto(
    Guid Id, CashMovementType Type, decimal Amount, string Reason,
    Guid CreatedByUserId, string CreatedByName, DateTime CreatedAtUtc);

public sealed record CreateCashMovementRequest(CashMovementType Type, decimal Amount, string Reason);

public interface IRegisterCashMovementService
{
    Task<RegisterCashMovementDto> CreateAsync(Guid sessionId, CreateCashMovementRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegisterCashMovementDto>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
```

Create `src/Negosio.Application/Registers/RegisterCashMovementValidator.cs`:

```csharp
using FluentValidation;

namespace Negosio.Application.Registers;

public sealed class CreateCashMovementRequestValidator : AbstractValidator<CreateCashMovementRequest>
{
    public CreateCashMovementRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).WithMessage("Amount must be greater than zero.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required.").MaximumLength(500);
    }
}
```

Create `src/Negosio.Application/Registers/RegisterCashMovementService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

public sealed class RegisterCashMovementService : IRegisterCashMovementService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IValidator<CreateCashMovementRequest> _validator;

    public RegisterCashMovementService(
        ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess,
        IValidator<CreateCashMovementRequest> validator)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
        _validator = validator;
    }

    public async Task<RegisterCashMovementDto> CreateAsync(
        Guid sessionId, CreateCashMovementRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        var session = await _db.RegisterSessions
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        if (session.OpenedByUserId != _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.CashMovementNotOwner, "This register session belongs to another user.");
        }

        if (session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.CashMovementSessionClosed, "This register session is not open.");
        }

        var movement = RegisterCashMovement.Create(
            tenantId, session.BranchId, session.Id, request.Type, request.Amount, request.Reason, _currentUser.UserId);
        _db.RegisterCashMovements.Add(movement);
        await _db.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(movement, cancellationToken);
    }

    public async Task<IReadOnlyList<RegisterCashMovementDto>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        var session = await _db.RegisterSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        // Owner/Admin: any session. Manager: their own branch. Cashier: only their own session.
        if (!BranchRoles.IsAllBranch(_currentUser.Role))
        {
            var assigned = (await _branchAccess.AssignedBranchIdAsync(cancellationToken))!.Value;
            if (session.BranchId != assigned)
            {
                throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");
            }

            if (_currentUser.Role == UserRole.Cashier && session.OpenedByUserId != _currentUser.UserId)
            {
                throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");
            }
        }

        var rows = await _db.RegisterCashMovements.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.RegisterSessionId == sessionId)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var names = await _db.Users.AsNoTracking()
            .Where(u => rows.Select(r => r.CreatedByUserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim(), cancellationToken);

        return rows.Select(m => new RegisterCashMovementDto(
            m.Id, m.Type, m.Amount, m.Reason, m.CreatedByUserId,
            names.GetValueOrDefault(m.CreatedByUserId, string.Empty), m.CreatedAtUtc)).ToList();
    }

    private async Task<RegisterCashMovementDto> ToDtoAsync(RegisterCashMovement m, CancellationToken cancellationToken)
    {
        var name = await _db.Users.AsNoTracking().Where(u => u.Id == m.CreatedByUserId)
            .Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        return new RegisterCashMovementDto(m.Id, m.Type, m.Amount, m.Reason, m.CreatedByUserId, name, m.CreatedAtUtc);
    }
}
```

Register `services.AddScoped<IRegisterCashMovementService, RegisterCashMovementService>();` and the validator (`services.AddScoped<IValidator<CreateCashMovementRequest>, CreateCashMovementRequestValidator>();`) in the same DI file as `IRegisterSessionService`.

- [ ] **Step 6: Add the endpoints**

In `src/Negosio.Api/Controllers/RegisterSessionsController.cs`, inject `IRegisterCashMovementService cashMovements` and add:

```csharp
[HttpPost("{id:guid}/cash-movements")]
[ProducesResponseType(typeof(RegisterCashMovementDto), StatusCodes.Status201Created)]
public async Task<ActionResult<RegisterCashMovementDto>> CreateCashMovement(
    Guid id, [FromBody] CreateCashMovementRequest request, CancellationToken cancellationToken)
{
    var created = await _cashMovements.CreateAsync(id, request, cancellationToken);
    return CreatedAtAction(nameof(ListCashMovements), new { id }, created);
}

[HttpGet("{id:guid}/cash-movements")]
[ProducesResponseType(typeof(IReadOnlyList<RegisterCashMovementDto>), StatusCodes.Status200OK)]
public async Task<ActionResult<IReadOnlyList<RegisterCashMovementDto>>> ListCashMovements(
    Guid id, CancellationToken cancellationToken)
    => Ok(await _cashMovements.ListAsync(id, cancellationToken));
```

(both inherit the controller's class-level `PosOperate` policy — no override needed, since Cashier/Manager/Owner/Admin should all reach these, with the fine-grained ownership/branch check enforced in-service exactly as `RegisterCashMovementService` does above.)

- [ ] **Step 7: Run to verify the tests pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter RegisterCashMovementTests`
Expected: 4 passed.

- [ ] **Step 8: Commit**

```bash
git add src/Negosio.Domain/Enums/CashMovementType.cs \
        src/Negosio.Domain/Entities/Pos/RegisterCashMovement.cs \
        src/Negosio.Infrastructure/Persistence/Configurations/RegisterCashMovementConfiguration.cs \
        src/Negosio.Infrastructure/Persistence/Migrations/Tenant/ \
        src/Negosio.Application/Abstractions/ITenantDbContext.cs \
        src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs \
        src/Negosio.Application/Registers/ \
        src/Negosio.Api/Controllers/RegisterSessionsController.cs \
        tests/Negosio.IntegrationTests/Registers/RegisterCashMovementTests.cs
git commit -m "feat(registers): add cash in/out movements"
```

---

## Task B4: `ApproverVerificationService` + `VoidSaleService` + void endpoint

This is the core of the phase. Build it as one task since the Cashier-without-grant path is meaningless without the approver verification it depends on — splitting them would leave an incomplete/untestable intermediate state.

**Files:**
- Create: `src/Negosio.Application/Sales/VoidEligibility.cs`
- Create: `src/Negosio.Application/Auth/ApproverVerificationService.cs`
- Create: `src/Negosio.Application/Sales/VoidSaleService.cs`
- Create: `src/Negosio.Application/Sales/VoidSaleValidator.cs`
- Modify: `src/Negosio.Application/Sales/SaleContracts.cs` (add `VoidSaleRequest`/`VoidSaleApprovalInput`, void + eligibility fields on `SaleDetailDto`/`SaleSummaryDto`)
- Modify: `src/Negosio.Application/Sales/SaleQueryService.cs` (populate the new fields — locate via `grep -rn "class SaleQueryService" src/Negosio.Application/Sales/`)
- Modify: `src/Negosio.Application/Sales/ReturnService.cs` (catch `DbUpdateConcurrencyException` — the other writer that can lose a race against a void)
- Modify: `src/Negosio.Application/Inventory/InventoryPosting.cs` (`ReverseForVoidAsync`, refactor shared restock logic)
- Modify: `src/Negosio.Domain/Enums/StockMovementType.cs` (append `SaleVoid = 10`)
- Modify: `src/Negosio.Api/Controllers/SalesController.cs` (`POST /api/sales/{id}/void`)
- Test: `tests/Negosio.IntegrationTests/Sales/VoidSaleTests.cs`
- Test: `tests/Negosio.IntegrationTests/Sales/VoidConcurrencyTests.cs`

**Interfaces:**
- Consumes: `ISalesVoidPermissionService.HasGrantAsync` (B2), `IBranchAccessResolver.AssignedBranchIdAsync`/`ResolveTargetBranchAsync` (existing), `IInventoryPosting` (extended here), `TimeProvider` (existing DI registration), `IPasswordHasher`/`IPlatformDbContext` (existing, same as `AuthService`), `Sale.RowVersion` (B1).
- Produces: `IVoidSaleService.VoidAsync(Guid saleId, VoidSaleRequest request, CancellationToken) -> Task<SaleDetailDto>`; `IApproverVerificationService.VerifyAsync(string email, string password, Guid saleBranchId, CancellationToken) -> Task<Guid>` (returns the approver's tenant `User.Id`); `VoidEligibility.Evaluate(Sale sale, bool sessionOpen, bool sameUtcDay, bool hasReturns) -> VoidEligibilityResult` (a pure static helper — **Task B4 only**, but its result shape, `VoidEligibilityResult(bool CanVoid, string? IneligibilityCode)`, is what `SaleDetailDto.CanVoid`/`VoidIneligibilityCode` surface to the frontend, and what both `VoidSaleService` and its post-conflict recheck use).

**Concurrency contract for this task (see the plan's Global Constraints):** `VoidAsync` captures one `nowUtc` from `TimeProvider` at the top and reuses it everywhere; opens its transaction, takes a pessimistic `WITH (UPDLOCK, HOLDLOCK)` lock on the sale's `RegisterSession` row as the very first statement inside it, *then* re-reads `hasReturns`/session-status/same-day fresh before evaluating eligibility; and wraps the final `SaveChangesAsync` in a catch for `DbUpdateConcurrencyException` that re-fetches the sale and throws its now-accurate eligibility error rather than a generic conflict.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/Negosio.IntegrationTests/Sales/VoidSaleTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class VoidSaleTests : IntegrationTest
{
    public VoidSaleTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private async Task<(Guid SaleId, Guid SessionId, Guid VariantId, Guid BranchId)> CheckoutOneSaleAsync(
        string cashierEmail, Guid branchId, Guid registerId)
    {
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, quantity: 10m);

        var cashierToken = await AddTenantUserTokenAsync(cashierEmail, UserRole.Cashier, branchId);
        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(registerId, 1000m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkout = await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 3m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 300m) }));
        var result = (await checkout.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        return (result.SaleId, session.Id, variantId, branchId);
    }

    private static HttpRequestMessage VoidRequest(Guid saleId, string reason, VoidSaleApprovalInput? approval = null) =>
        new(HttpMethod.Post, $"/api/sales/{saleId}/void") { Content = JsonContent.Create(new VoidSaleRequest(reason, approval)) };

    [Fact]
    public async Task Owner_voids_directly_and_restores_inventory()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, variantId, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        var before = await GetInventoryQuantityAsync(branchId, variantId);
        var res = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await res.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.Sale.Status.Should().Be(SaleStatus.Voided);
        detail.Sale.SaleNumber.Should().Be("0000001"); // unchanged
        detail.VoidedByUserId.Should().NotBeNull();
        detail.ApprovedByUserId.Should().BeNull();

        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(before + 3m);
    }

    [Fact]
    public async Task Manager_voids_own_branch_but_not_another_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);
        var mainRegister = await CreateRegisterAsync(main, "M1", "M1");
        var (mainSaleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", main, mainRegister.Id);

        var bgcManagerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, bgc.Id);
        Authorize(bgcManagerToken);
        var res = await Client.SendAsync(VoidRequest(mainSaleId, "Wrong branch attempt"));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cashier_with_grant_voids_directly_with_reason_only()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);
        var cashierId = await GetUserIdFromTokenAsync(await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId));

        Authorize(owner.AccessToken);
        await Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/staff/{cashierId}/permissions")
        {
            Content = JsonContent.Create(new ChangeStaffPermissionsRequest(true))
        });

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Duplicate transaction"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await res.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.ApprovedByUserId.Should().BeNull();
        detail.VoidedByUserId.Should().Be(cashierId);
    }

    [Fact]
    public async Task Cashier_without_grant_requires_approval_then_succeeds_with_valid_manager_credentials()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var cashierId = await GetUserIdFromTokenAsync(cashierToken);

        var managerId = await CreateManagerAsync("mgr@example.com", "Manager123!", branchId);

        Authorize(cashierToken);
        var denied = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method"));
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await denied.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidApprovalRequired);

        var approved = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method",
            new VoidSaleApprovalInput("mgr@example.com", "Manager123!")));

        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await approved.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        detail.VoidedByUserId.Should().Be(cashierId);
        detail.ApprovedByUserId.Should().Be(managerId);
    }

    [Fact]
    public async Task Wrong_manager_password_is_rejected_and_nothing_changes()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, variantId, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        await CreateManagerAsync("mgr@example.com", "Manager123!", branchId);

        Authorize(cashierToken);
        var before = await GetInventoryQuantityAsync(branchId, variantId);
        var res = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method",
            new VoidSaleApprovalInput("mgr@example.com", "WrongPassword!")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.InvalidApproverCredentials);
        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(before);
    }

    [Fact]
    public async Task Manager_from_a_different_branch_cannot_approve()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var bgc = await CreateBranchAsync("BGC", "BGC");
        var main = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(main, "M1", "M1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", main, register.Id);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, main);
        await CreateManagerAsync("bgc.mgr@example.com", "Manager123!", bgc.Id);

        Authorize(cashierToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Wrong payment method",
            new VoidSaleApprovalInput("bgc.mgr@example.com", "Manager123!")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidApproverWrongBranch);
    }

    [Fact]
    public async Task Already_voided_sale_cannot_be_voided_again()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, variantId, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        await Client.SendAsync(VoidRequest(saleId, "First void"));
        var afterFirst = await GetInventoryQuantityAsync(branchId, variantId);

        var second = await Client.SendAsync(VoidRequest(saleId, "Second attempt"));

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.SaleNotVoidable);
        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(afterFirst); // not restored twice
    }

    [Fact]
    public async Task Sale_with_a_return_cannot_be_voided()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        var items = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{saleId}", TestJson.Options);
        var firstItemId = items!.Items[0].Id;
        await Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new CreateReturnRequest(
            new[] { new ReturnLineInput(firstItemId, 1m, true) }, "Customer changed mind", PaymentMethod.Cash, null));

        var res = await Client.SendAsync(VoidRequest(saleId, "Attempt after return"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.SaleHasReturns);
    }

    [Fact]
    public async Task Voided_sale_cannot_later_be_returned_against()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        Authorize(owner.AccessToken);
        var items = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{saleId}", TestJson.Options);
        var firstItemId = items!.Items[0].Id;
        await Client.SendAsync(VoidRequest(saleId, "Wrong payment method"));

        var res = await Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new CreateReturnRequest(
            new[] { new ReturnLineInput(firstItemId, 1m, true) }, "Too late", PaymentMethod.Cash, null));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.ReturnNotAllowed);
    }

    [Fact]
    public async Task Voiding_a_sale_whose_session_is_already_closed_is_rejected()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, sessionId, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);
        await Client.PostAsJsonAsync($"/api/register-sessions/{sessionId}/close", new CloseRegisterSessionRequest(1300m));

        Authorize(owner.AccessToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Too late"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidSessionClosed);
    }

    [Fact]
    public async Task Voiding_a_sale_from_a_prior_day_is_rejected()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var (saleId, _, _, _) = await CheckoutOneSaleAsync("cara@example.com", branchId, register.Id);

        await BackdateSaleCompletedAtAsync(saleId, DateTime.UtcNow.AddDays(-1));

        Authorize(owner.AccessToken);
        var res = await Client.SendAsync(VoidRequest(saleId, "Too late"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be(ErrorCodes.VoidCutoffExpired);
    }
}
```

This assumes three more `IntegrationTest` base helpers beyond what already exists (`GetUserIdFromTokenAsync` from B2 Step 1):

```csharp
/// <summary>Creates an active Manager in the given branch with a known password, returns their user id.</summary>
protected async Task<Guid> CreateManagerAsync(string email, string password, Guid branchId)
{
    // Mirrors AddTenantUserTokenAsync's own provisioning path, but needs a caller-chosen password
    // (AddTenantUserTokenAsync likely mints a random one) — read that helper first and factor out
    // the shared piece rather than duplicating tenant/platform user creation.
}

/// <summary>Reads a variant's current on-hand quantity in a branch, via the existing inventory endpoint.
/// The DTO type name/field names here (`InventoryItemDto`/`QuantityOnHand`) are a best guess from the
/// Phase 2 naming convention, not confirmed by research for this plan — grep the actual Inventory
/// contracts file (`src/Negosio.Application/Inventory/`) for the real type/field names before using this.</summary>
protected async Task<decimal> GetInventoryQuantityAsync(Guid branchId, Guid variantId)
{
    var page = await Client.GetFromJsonAsync<PagedResult<InventoryItemDto>>(
        $"/api/inventory?branchId={branchId}&pageSize=200", TestJson.Options);
    return page!.Items.Single(i => i.ProductVariantId == variantId).QuantityOnHand;
}

/// <summary>Test-only backdate of a sale's CompletedAtUtc, for cutoff testing — raw SQL, bypasses the domain.</summary>
protected async Task BackdateSaleCompletedAtAsync(Guid saleId, DateTime completedAtUtc)
{
    using var scope = Factory.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
    await using var db = await factory.CreateAsync(CurrentTenantId); // use whatever field/property already tracks the active tenant id in this base class
    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sales SET CompletedAtUtc = {completedAtUtc} WHERE Id = {saleId}");
}
```

Read `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs` first — reuse whatever it already exposes (tenant id tracking, `AddTenantUserTokenAsync`'s exact provisioning code) rather than guessing; adapt the three helpers above to fit its actual shape, keeping their behavior as described.

Also create `tests/Negosio.IntegrationTests/Sales/VoidConcurrencyTests.cs` — the three concurrent-race tests the plan's Global Constraints require. These fire two requests via `Task.WhenAll` rather than forcing an exact interleaving; the assertions check the *invariant* (exactly one winner, no double-reversal, no inconsistent reconciliation), which holds regardless of how the scheduler actually interleaves the two requests — a real correctness guarantee, not a timing-dependent flake:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Sales;

public class VoidConcurrencyTests : IntegrationTest
{
    public VoidConcurrencyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    // Explicit per-request Authorization header, NOT the shared Client.DefaultRequestHeaders the
    // Authorize(token) helper mutates — two concurrent requests as different actors must not race
    // on that shared mutable state. Clear Client.DefaultRequestHeaders.Authorization = null before
    // using this in a test (verify against the real Authorize() implementation first).
    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Two_concurrent_voids_on_the_same_sale_never_double_restore_inventory()
    {
        Client.DefaultRequestHeaders.Authorization = null;
        var owner = await RegisterLoginAndAuthorizeAsync();
        Client.DefaultRequestHeaders.Authorization = null; // undo RegisterLoginAndAuthorizeAsync's own Authorize() side effect
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, quantity: 10m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkoutRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/pos/checkout", cashierToken,
            new CheckoutRequest(branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 3m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 300m) })));
        var sale = (await checkoutRes.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        var beforeVoid = await GetInventoryQuantityAsync(branchId, variantId);

        HttpRequestMessage VoidRequest() => AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/void", owner.AccessToken,
            new VoidSaleRequest("Concurrent void attempt"));

        var results = await Task.WhenAll(Client.SendAsync(VoidRequest()), Client.SendAsync(VoidRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
        (await GetInventoryQuantityAsync(branchId, variantId)).Should().Be(beforeVoid + 3m); // restored exactly once
    }

    [Fact]
    public async Task Concurrent_void_and_return_on_the_same_sale_never_both_succeed()
    {
        Client.DefaultRequestHeaders.Authorization = null;
        var owner = await RegisterLoginAndAuthorizeAsync();
        Client.DefaultRequestHeaders.Authorization = null;
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, quantity: 10m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkoutRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/pos/checkout", cashierToken,
            new CheckoutRequest(branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 2m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) })));
        var sale = (await checkoutRes.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        var detailRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/sales/{sale.SaleId}", owner.AccessToken));
        var detail = (await detailRes.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        var firstItemId = detail.Items[0].Id;

        var voidTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/void", owner.AccessToken,
            new VoidSaleRequest("Concurrent void")));
        var returnTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/returns", owner.AccessToken,
            new CreateReturnRequest(new[] { new ReturnLineInput(firstItemId, 1m, true) }, "Concurrent return", PaymentMethod.Cash, null)));

        var results = await Task.WhenAll(voidTask, returnTask);

        // Exactly one of the two can succeed — a sale can never end up both Voided and Refunded.
        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);

        var finalRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/sales/{sale.SaleId}", owner.AccessToken));
        var final = (await finalRes.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;
        final.Sale.Status.Should().BeOneOf(SaleStatus.Voided, SaleStatus.PartiallyRefunded, SaleStatus.Refunded);
    }
}
```

(This file deliberately covers only the two *same-row* races — double-void and void-vs-return — since both are protected by `Sale.RowVersion` alone, landing entirely in this task. The third required race, void-vs-session-close, needs `RegisterSessionService.ReconcileAndCloseAsync` to also take the pessimistic session lock *before its own reconciliation reads* — not just before its final write — which is a change Task B5 makes to that exact method; that test is added there, in B5's own test file, immediately after B5 adds the matching lock. Landing it here instead would make this task's test suite depend on a task that hasn't run yet.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "VoidSaleTests|VoidConcurrencyTests"`
Expected: compile errors — none of `VoidSaleRequest`, `VoidSaleApprovalInput`, `SaleDetailDto.VoidedByUserId`/`ApprovedByUserId`, or the void endpoint exist yet.

- [ ] **Step 3: Append `SaleVoid` to `StockMovementType` and extend `IInventoryPosting`**

In `src/Negosio.Domain/Enums/StockMovementType.cs`, append (never renumber the existing 1-9):

```csharp
    Waste = 9,
    SaleVoid = 10
```

In `src/Negosio.Application/Inventory/InventoryPosting.cs`, add to the interface:

```csharp
/// <summary>Restore stock for a voided sale line (never fails, mirrors RestockForReturnAsync).</summary>
Task ReverseForVoidAsync(
    Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
    Guid saleId, Guid userId, CancellationToken cancellationToken = default);
```

Refactor the implementation to share one private helper (DRY — `RestockForReturnAsync` and `ReverseForVoidAsync` differ only in `StockMovementType` and the reference type/id):

```csharp
public Task RestockForReturnAsync(
    Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
    Guid saleReturnId, Guid userId, CancellationToken cancellationToken = default) =>
    RestockAsync(tenantId, branchId, productVariantId, quantity, userId,
        StockMovementType.Return, referenceType: "Return", referenceId: saleReturnId, cancellationToken);

public Task ReverseForVoidAsync(
    Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
    Guid saleId, Guid userId, CancellationToken cancellationToken = default) =>
    RestockAsync(tenantId, branchId, productVariantId, quantity, userId,
        StockMovementType.SaleVoid, referenceType: "Sale", referenceId: saleId, cancellationToken);

private async Task RestockAsync(
    Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity, Guid userId,
    StockMovementType type, string referenceType, Guid referenceId, CancellationToken cancellationToken)
{
    var now = DateTime.UtcNow;

    var affected = await _db.BranchInventories
        .Where(i => i.TenantId == tenantId && i.BranchId == branchId && i.ProductVariantId == productVariantId)
        .ExecuteUpdateAsync(
            s => s.SetProperty(i => i.QuantityOnHand, i => i.QuantityOnHand + quantity)
                  .SetProperty(i => i.UpdatedAtUtc, _ => now),
            cancellationToken);

    decimal after;
    decimal before;
    if (affected == 0)
    {
        var created = BranchInventory.Create(tenantId, branchId, productVariantId, 0m);
        created.ApplyAdjustment(quantity);
        _db.BranchInventories.Add(created);
        before = 0m;
        after = quantity;
    }
    else
    {
        after = await _db.BranchInventories
            .Where(i => i.TenantId == tenantId && i.BranchId == branchId && i.ProductVariantId == productVariantId)
            .Select(i => i.QuantityOnHand)
            .SingleAsync(cancellationToken);
        before = after - quantity;
    }

    _db.StockMovements.Add(StockMovement.Create(
        tenantId, branchId, productVariantId, type, quantity, before, after, reason: null, userId,
        referenceType, referenceId));
}
```

Delete the old separate bodies of `RestockForReturnAsync` — this replaces them, it does not add alongside them.

- [ ] **Step 4: Add the shared eligibility helper**

Create `src/Negosio.Application/Sales/VoidEligibility.cs`:

```csharp
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

public sealed record VoidEligibilityResult(bool CanVoid, string? IneligibilityCode);

/// <summary>
/// The single source of truth for "can this sale be voided right now" — used both by
/// <see cref="VoidSaleService"/> (to reject with a typed error) and by <see cref="SaleQueryService"/>
/// (to surface `CanVoid`/`VoidIneligibilityCode` on the DTO so the frontend can hide/disable the
/// button without duplicating this ordering). Checked in exactly this order for every actor,
/// including Owner — authorization never overrides domain eligibility.
/// </summary>
public static class VoidEligibility
{
    public static VoidEligibilityResult Evaluate(Sale sale, bool sessionOpen, bool sameUtcDay, bool hasReturns)
    {
        if (sale.Status != SaleStatus.Completed)
        {
            return new VoidEligibilityResult(false, ErrorCodes.SaleNotVoidable);
        }

        if (hasReturns)
        {
            return new VoidEligibilityResult(false, ErrorCodes.SaleHasReturns);
        }

        if (!sessionOpen)
        {
            return new VoidEligibilityResult(false, ErrorCodes.VoidSessionClosed);
        }

        if (!sameUtcDay)
        {
            return new VoidEligibilityResult(false, ErrorCodes.VoidCutoffExpired);
        }

        return new VoidEligibilityResult(true, null);
    }
}
```

- [ ] **Step 5: Add the approver reverification service**

Create `src/Negosio.Application/Auth/ApproverVerificationService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Auth;

public interface IApproverVerificationService
{
    /// <summary>
    /// Reverifies a Manager/Admin/Owner's password without issuing a JWT. Returns the approver's
    /// tenant User id on success. One-shot — nothing about this call is cached or reusable for a
    /// later request.
    /// </summary>
    Task<Guid> VerifyAsync(string approverEmail, string approverPassword, Guid saleBranchId, CancellationToken cancellationToken = default);
}

public sealed class ApproverVerificationService : IApproverVerificationService
{
    private readonly IPlatformDbContext _platform;
    private readonly ITenantDbContext _tenant;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUser _currentUser;
    private Lazy<string>? _timingEqualizerHash;

    public ApproverVerificationService(
        IPlatformDbContext platform, ITenantDbContext tenant, IPasswordHasher passwordHasher, ICurrentUser currentUser)
    {
        _platform = platform;
        _tenant = tenant;
        _passwordHasher = passwordHasher;
        _currentUser = currentUser;
    }

    public async Task<Guid> VerifyAsync(
        string approverEmail, string approverPassword, Guid saleBranchId, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = User.NormalizeEmail(approverEmail);
        var login = await _platform.PlatformUserLogins
            .SingleOrDefaultAsync(l => l.EmailNormalized == normalizedEmail, cancellationToken);

        // Same timing-equalizer approach as AuthService.LoginAsync — verify a hash either way.
        var hashToCheck = login?.PasswordHash ?? GetTimingEqualizerHash();
        var passwordValid = _passwordHasher.Verify(approverPassword, hashToCheck);

        // Also reject a login that resolves to a different tenant — never reveal that distinction.
        if (login is null || !passwordValid || login.TenantId != _currentUser.TenantId || !login.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidApproverCredentials, "Invalid manager credentials.");
        }

        var approver = await _tenant.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.TenantId == _currentUser.TenantId && u.Id == login.Id, cancellationToken);
        if (approver is null || !approver.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidApproverCredentials, "Invalid manager credentials.");
        }

        if (approver.Role is not (UserRole.Manager or UserRole.Admin or UserRole.Owner))
        {
            throw new ForbiddenAppException(ErrorCodes.VoidApproverNotAuthorized, "This account cannot approve voids.");
        }

        if (approver.Role == UserRole.Manager)
        {
            if (approver.BranchId != saleBranchId)
            {
                throw new ForbiddenAppException(ErrorCodes.VoidApproverWrongBranch, "This manager cannot approve voids for this branch.");
            }

            var branchActive = await _tenant.Branches.AsNoTracking()
                .AnyAsync(b => b.Id == saleBranchId && b.IsActive, cancellationToken);
            if (!branchActive)
            {
                throw new ForbiddenAppException(ErrorCodes.VoidApproverWrongBranch, "This manager cannot approve voids for this branch.");
            }
        }

        return approver.Id;
    }

    private string GetTimingEqualizerHash() =>
        (_timingEqualizerHash ??= new Lazy<string>(() => _passwordHasher.Hash("timing-equalizer"))).Value;
}
```

Register `services.AddScoped<IApproverVerificationService, ApproverVerificationService>();` in the same DI block as `IAuthService`.

- [ ] **Step 6: Add the void contracts and validator**

In `src/Negosio.Application/Sales/SaleContracts.cs`, add:

```csharp
public sealed record VoidSaleApprovalInput(string ApproverEmail, string ApproverPassword);

public sealed record VoidSaleRequest(string Reason, VoidSaleApprovalInput? Approval = null);

public interface IVoidSaleService
{
    Task<SaleDetailDto> VoidAsync(Guid saleId, VoidSaleRequest request, CancellationToken cancellationToken = default);
}
```

Extend `SaleDetailDto`'s record with five trailing fields (append after `Returns`):

```csharp
public sealed record SaleDetailDto(
    SaleSummaryDto Sale,
    Guid RegisterSessionId,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal AmountPaid,
    decimal ChangeDue,
    DateTime? CompletedAtUtc,
    IReadOnlyList<SaleItemDto> Items,
    IReadOnlyList<SalePaymentDto> Payments,
    IReadOnlyList<SaleReturnDto> Returns,
    Guid? VoidedByUserId,
    string? VoidedByName,
    Guid? ApprovedByUserId,
    string? ApprovedByName,
    string? VoidReason,
    DateTime? VoidedAtUtc,
    bool CanVoid,
    string? VoidIneligibilityCode);
```

(every existing call site that constructs a `SaleDetailDto` — in `SaleQueryService` — needs updating for the new positional fields; Step 8 does that.)

Create `src/Negosio.Application/Sales/VoidSaleValidator.cs`:

```csharp
using FluentValidation;

namespace Negosio.Application.Sales;

public sealed class VoidSaleRequestValidator : AbstractValidator<VoidSaleRequest>
{
    public VoidSaleRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A void reason is required.").MaximumLength(500);

        When(x => x.Approval is not null, () =>
        {
            RuleFor(x => x.Approval!.ApproverEmail).NotEmpty().EmailAddress();
            RuleFor(x => x.Approval!.ApproverPassword).NotEmpty();
        });
    }
}
```

- [ ] **Step 7: Implement `VoidSaleService`, concurrency-safe**

Create `src/Negosio.Application/Sales/VoidSaleService.cs`. Note the structure versus a naive version: the role guard runs first (fail fast, no DB work for a role that could never void); eligibility is checked once up front only to fail fast for the *common* case (avoids resolving approver credentials for an obviously-ineligible sale), but is **re-checked fresh inside the transaction, after the pessimistic session lock**, since that first check can be stale by the time the transaction runs; the pessimistic lock is what makes the second check authoritative; and `DbUpdateConcurrencyException` on the final save is the safety net for the Sale-row races (double-void, void-vs-return) the lock doesn't cover:

```csharp
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

public sealed class VoidSaleService : IVoidSaleService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<VoidSaleRequest> _validator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly ISalesVoidPermissionService _permissions;
    private readonly IApproverVerificationService _approverVerification;
    private readonly IInventoryPosting _inventory;
    private readonly ISaleQueryService _saleQuery;
    private readonly TimeProvider _timeProvider;

    public VoidSaleService(
        ITenantDbContext db, ICurrentUser currentUser, IValidator<VoidSaleRequest> validator,
        IBranchAccessResolver branchAccess, ISalesVoidPermissionService permissions,
        IApproverVerificationService approverVerification, IInventoryPosting inventory,
        ISaleQueryService saleQuery, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
        _branchAccess = branchAccess;
        _permissions = permissions;
        _approverVerification = approverVerification;
        _inventory = inventory;
        _saleQuery = saleQuery;
        _timeProvider = timeProvider;
    }

    public async Task<SaleDetailDto> VoidAsync(Guid saleId, VoidSaleRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        // Explicit, defense-in-depth role gate — never rely solely on the controller's SalesView
        // policy, and never treat "not Owner/Admin/Manager" as an implicit synonym for Cashier.
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot void sales.");
        }

        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        // One nowUtc for this entire attempt — reused for both the same-day eligibility check and
        // the VoidedAtUtc stamp, so the two can never disagree even under a slow request.
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var sale = await _db.Sales.Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        // Branch-scoped users get 404 (not 403) on a foreign sale, matching ReturnService.
        var assignedBranch = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assignedBranch is { } branchId && branchId != sale.BranchId)
        {
            throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        }

        // First-pass eligibility check — fast-fails the common case before resolving approver
        // credentials or opening a transaction. NOT authoritative by itself; re-checked below.
        await EnsureEligibleAsync(sale, tenantId, nowUtc, cancellationToken);

        var (voidedByUserId, approvedByUserId) = await ResolveActorAsync(sale.BranchId, request, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Pessimistic lock on the session row — the FIRST statement inside the transaction, before
        // any other read. RowVersion (Sale) cannot protect this cross-entity race: Void never writes
        // to RegisterSessions, so there's no natural optimistic-concurrency collision to detect
        // against a concurrent close. This lock instead fully serializes the two operations: whoever
        // acquires it first runs their entire read-then-write to completion before the other can even
        // begin reading. RegisterSessionService.ReconcileAndCloseAsync takes the identical lock.
        await _db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {sale.RegisterSessionId}")
            .ToListAsync(cancellationToken);

        // Authoritative re-check, now that the session row is locked for the rest of this transaction.
        await EnsureEligibleAsync(sale, tenantId, nowUtc, cancellationToken);

        var lineVariantIds = sale.Items.Select(i => i.ProductVariantId).ToList();
        var trackedVariantIds = (await _db.ProductVariants.AsNoTracking()
            .Where(v => v.TenantId == tenantId && lineVariantIds.Contains(v.Id))
            .Join(_db.Products.Where(p => p.TrackInventory), v => v.ProductId, p => p.Id, (v, _) => v.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        foreach (var item in sale.Items.Where(i => trackedVariantIds.Contains(i.ProductVariantId)))
        {
            await _inventory.ReverseForVoidAsync(
                tenantId, sale.BranchId, item.ProductVariantId, item.Quantity, sale.Id, _currentUser.UserId, cancellationToken);
        }

        sale.Void(voidedByUserId, request.Reason, approvedByUserId, nowUtc);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Sale.RowVersion caught a race this lock doesn't cover — another void, or a Return,
            // committed between our load and our write. Roll back (the inventory reversal above
            // rolls back with it — it's the same DB transaction, so it's never left half-applied),
            // then report the sale's actual current state instead of a generic conflict.
            await transaction.RollbackAsync(cancellationToken);
            await EnsureEligibleAsync(null, tenantId, nowUtc, cancellationToken, saleId);
            throw new InvalidOperationException("Unreachable — EnsureEligibleAsync always throws when re-evaluating a lost race.");
        }

        await transaction.CommitAsync(cancellationToken);

        return await _saleQuery.GetAsync(saleId, cancellationToken);
    }

    /// <summary>
    /// Re-derives and evaluates eligibility from a fresh read. Pass the already-loaded
    /// <paramref name="sale"/> for the pre-lock fast-fail and the post-lock authoritative check
    /// (same tracked instance, so `sale.Items` stays populated); pass <c>null</c> with
    /// <paramref name="saleId"/> instead when re-evaluating after a lost `DbUpdateConcurrencyException`
    /// race, since the tracked instance's in-memory state is no longer trustworthy at that point.
    /// Always throws — never returns normally — since every call site only calls this when it
    /// already needs the (possibly now-different) typed error.
    /// </summary>
    private async Task EnsureEligibleAsync(
        Sale? sale, Guid tenantId, DateTime nowUtc, CancellationToken cancellationToken, Guid? saleId = null)
    {
        var current = sale ?? await _db.Sales.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.Id == saleId!.Value, cancellationToken);

        var hasReturns = await _db.SaleReturns.AnyAsync(r => r.TenantId == tenantId && r.SaleId == current.Id, cancellationToken);
        var session = await _db.RegisterSessions.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.Id == current.RegisterSessionId, cancellationToken);
        var sessionOpen = session.Status == RegisterSessionStatus.Open;
        var sameUtcDay = current.CompletedAtUtc is { } completedAt && completedAt.Date == nowUtc.Date;

        var eligibility = VoidEligibility.Evaluate(current, sessionOpen, sameUtcDay, hasReturns);
        if (!eligibility.CanVoid)
        {
            throw new BusinessRuleException(eligibility.IneligibilityCode!, IneligibilityMessage(eligibility.IneligibilityCode!));
        }
        // eligibility.CanVoid == true here should only happen on the two authoritative pre-write
        // calls (which then proceed to write); if called after a lost race, CanVoid should always
        // be false (the very state change that beat us is what makes it ineligible) — if it somehow
        // isn't, that's a bug worth a loud failure rather than a silent no-op, hence no return path.
    }

    private async Task<(Guid VoidedBy, Guid? ApprovedBy)> ResolveActorAsync(
        Guid saleBranchId, VoidSaleRequest request, CancellationToken cancellationToken)
    {
        var role = _currentUser.Role;

        if (role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            // Manager's branch match was already asserted by the 404 guard above (AssignedBranchIdAsync).
            return (_currentUser.UserId, null);
        }

        // role == UserRole.Cashier, explicitly — the top-of-method guard already rejected every
        // other role, so this is never reached as a fallback for "anything else."
        if (await _permissions.HasGrantAsync(_currentUser.UserId, cancellationToken))
        {
            return (_currentUser.UserId, null);
        }

        if (request.Approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.VoidApprovalRequired,
                "You don't have permission to void completed sales. An authorized Manager, Admin, or Owner must approve this void.");
        }

        var approverId = await _approverVerification.VerifyAsync(
            request.Approval.ApproverEmail, request.Approval.ApproverPassword, saleBranchId, cancellationToken);
        return (_currentUser.UserId, approverId);
    }

    private static string IneligibilityMessage(string code) => code switch
    {
        ErrorCodes.SaleNotVoidable => "This sale cannot be voided.",
        ErrorCodes.SaleHasReturns => "This sale has returns against it and cannot be voided.",
        ErrorCodes.VoidSessionClosed => "This sale can no longer be voided because its register session has already been closed. Use the return/refund process instead.",
        ErrorCodes.VoidCutoffExpired => "This sale is past the void cutoff. Use the return/refund process instead.",
        _ => "This sale cannot be voided."
    };
}
```

A note on the `catch (DbUpdateConcurrencyException)` block above: `EnsureEligibleAsync` always throws when eligibility is false, which it always will be immediately after losing a race (the very change that beat us — another void, or a return — is what makes the sale newly ineligible). The trailing `throw new InvalidOperationException("Unreachable...")` exists only so the method's control flow is provably exhaustive to the compiler; it should never actually execute, and its presence is a deliberate loud-failure signal if that assumption is ever wrong, rather than a silent success path.

Register `services.AddScoped<IVoidSaleService, VoidSaleService>();` and `services.AddScoped<IValidator<VoidSaleRequest>, VoidSaleRequestValidator>();` alongside the other Sales services (find where `IReturnService` is registered).

- [ ] **Step 7b: Make `ReturnService` react correctly to losing a race against a void**

`ReturnService.CreateReturnAsync` currently does a plain `await _db.SaveChangesAsync(cancellationToken)` inside its transaction. Now that `Sale` carries a `RowVersion`, that call can throw `DbUpdateConcurrencyException` if a concurrent void committed first. Wrap it:

```csharp
try
{
    await _db.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateConcurrencyException)
{
    var fresh = await _db.Sales.AsNoTracking()
        .SingleAsync(s => s.TenantId == tenantId && s.Id == sale.Id, cancellationToken);
    throw new BusinessRuleException(ErrorCodes.ReturnNotAllowed,
        fresh.Status == SaleStatus.Voided
            ? "This sale was voided and cannot be returned against."
            : "This sale cannot be returned against.");
}
await transaction.CommitAsync(cancellationToken);
```

(the surrounding `await using var transaction = ...` already rolls back automatically on the exception path before this catch's own logic runs its re-fetch — no explicit `RollbackAsync` needed here, since we don't `CommitAsync` before the catch fires, and the `await using` disposal handles the rest.)

- [ ] **Step 8: Wire `SaleQueryService.GetAsync`/`ListAsync` to populate the new fields**

Open `src/Negosio.Application/Sales/SaleQueryService.cs` first to see its exact current query shape. In `GetAsync`, after loading the sale (and its items, for `hasReturns`/eligibility), add the same three facts `VoidSaleService` computes (`hasReturns`, `sessionOpen`, `sameUtcDay` — inject `TimeProvider` here too) and call `VoidEligibility.Evaluate(...)` to populate `CanVoid`/`VoidIneligibilityCode`. Resolve `VoidedByName`/`ApprovedByName` the same way `CashierName` is already resolved (a `Users` lookup by id), only when the corresponding `*UserId` is non-null. Pass all of it into the now-11-field-longer `SaleDetailDto` constructor call.

- [ ] **Step 9: Add the endpoint**

In `src/Negosio.Api/Controllers/SalesController.cs`, inject `IVoidSaleService voidSale` and add, after `CreateReturn`:

```csharp
[HttpPost("{id:guid}/void")]
[ProducesResponseType(typeof(SaleDetailDto), StatusCodes.Status200OK)]
public async Task<ActionResult<SaleDetailDto>> Void(
    Guid id, [FromBody] VoidSaleRequest request, CancellationToken cancellationToken)
    => Ok(await _voidSale.VoidAsync(id, request, cancellationToken));
```

(No policy override — the controller's class-level `SalesView` already restricts this to Owner/Admin/Manager/Cashier, which is exactly the void-participating role set; InventoryStaff/Viewer/KitchenStaff never reach the action method at all.)

- [ ] **Step 10: Run to verify the tests pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "VoidSaleTests|VoidConcurrencyTests"`
Expected: 13 passed (11 from `VoidSaleTests` + 2 from `VoidConcurrencyTests`). If `BackdateSaleCompletedAtAsync`/`CreateManagerAsync`/`GetInventoryQuantityAsync` needed adjustment to fit the real `IntegrationTest` base class shape (Step 1's caveat), iterate until green — do not weaken an assertion to make a test pass. If the concurrency tests are flaky (pass most runs, occasionally not), that itself is a signal the locking/optimistic-concurrency logic isn't actually correct — do not retry-until-green; debug the race, don't paper over it.

Also run the full suite once to confirm nothing regressed:

Run: `dotnet test tests/Negosio.IntegrationTests --filter "ReturnService|Checkout|RegisterSession"`
Expected: all still passing (the `InventoryPosting` refactor, `SaleDetailDto` field additions, and `ReturnService`'s new concurrency catch must not change existing behavior).

- [ ] **Step 11: Commit**

```bash
git add src/Negosio.Domain/Enums/StockMovementType.cs \
        src/Negosio.Application/Inventory/InventoryPosting.cs \
        src/Negosio.Application/Sales/ \
        src/Negosio.Application/Auth/ApproverVerificationService.cs \
        src/Negosio.Api/Controllers/SalesController.cs \
        tests/Negosio.IntegrationTests/Sales/VoidSaleTests.cs \
        tests/Negosio.IntegrationTests/Sales/VoidConcurrencyTests.cs \
        tests/Negosio.IntegrationTests/Infrastructure/
git commit -m "feat(sales): implement direct and manager-approved void"
```

---

## Task B5: Reconciliation formula — voided sales + cash movements

**Files:**
- Create: `src/Negosio.Domain/Entities/Pos/CashReconciliationBreakdown.cs`
- Modify: `src/Negosio.Domain/Entities/Pos/RegisterSession.cs` (breakdown columns, `Close()` signature change)
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/RegisterSessionConfiguration.cs` (locate via `grep -rn "class RegisterSessionConfiguration" src/Negosio.Infrastructure/`)
- Create: EF migration under `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/`
- Modify: `src/Negosio.Application/Registers/RegisterContracts.cs` (`RegisterSessionDto` breakdown fields)
- Modify: `src/Negosio.Application/Registers/RegisterSessionService.cs` (`ReconcileAndCloseAsync`, `ProjectAsync`)
- Modify: `tests/Negosio.UnitTests/Pos/RegisterSessionTests.cs` (existing `Close(...)` call sites — signature changes, this is a breaking change to fix, not a regression to ignore)
- Test: `tests/Negosio.IntegrationTests/Registers/ReconciliationTests.cs`
- Test: `tests/Negosio.IntegrationTests/Registers/CloseVoidConcurrencyTests.cs`

**Interfaces:**
- Consumes: `_db.RegisterCashMovements` (B3), `Sale.Status == Voided` (B1) — no new service dependency, `RegisterSessionService` already has `ITenantDbContext`.
- Produces: `RegisterSession.Close(Guid closedByUserId, decimal closingCash, decimal expectedCash, CashReconciliationBreakdown breakdown)` — **note the signature changed** from the existing 3-arg `Close(closedByUserId, closingCash, expectedCash)`; every caller (only `RegisterSessionService.ReconcileAndCloseAsync` and the existing unit tests) must be updated in this same task.

**Concurrency contract for this task (see the plan's Global Constraints and Task B4):** `ReconcileAndCloseAsync` takes the identical `WITH (UPDLOCK, HOLDLOCK)` lock on the session row that `VoidSaleService.VoidAsync` (B4) already takes, as the very first statement — **before any of its reconciliation SUM queries**, not just before its final write. Locking only around the write would still let a concurrent void's changes land *after* this method already computed its sums from a stale snapshot, even though the two writes themselves would end up correctly ordered — the read has to be inside the same serialization point as the write for the reconciliation numbers to be trustworthy.

- [ ] **Step 1: Write the failing integration test**

Create `tests/Negosio.IntegrationTests/Registers/ReconciliationTests.cs`:

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

public class ReconciliationTests : IntegrationTest
{
    public ReconciliationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Expected_cash_accounts_for_opening_sales_void_refund_and_cash_movements()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantA) = await SeedStockedProductAsync(branchId, category.Id, quantity: 20m, sellingPrice: 500m);
        var (_, variantB) = await SeedStockedProductAsync(branchId, category.Id, quantity: 20m, sellingPrice: 200m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);
        var session = (await (await Client.PostAsJsonAsync("/api/register-sessions/open",
            new OpenRegisterSessionRequest(register.Id, 5000m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        // Sale 1: ₱500 cash — this one gets voided.
        var sale1 = (await (await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantA, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) })))
            .Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        // Sale 2: ₱200 cash — this one stays completed.
        await Client.PostAsJsonAsync("/api/pos/checkout", new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantB, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));

        await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashIn, 500m, "Float top-up"));
        await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/cash-movements",
            new CreateCashMovementRequest(CashMovementType.CashOut, 1000m, "Bank deposit"));

        await Client.PostAsJsonAsync($"/api/sales/{sale1.SaleId}/void", new VoidSaleRequest("Wrong item rung up"));

        var closed = (await (await Client.PostAsJsonAsync($"/api/register-sessions/{session.Id}/close",
            new CloseRegisterSessionRequest(4700m))).Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        // 5000 opening + 700 gross cash sales (500 + 200) - 500 voided - 0 refunds + 500 cash in - 1000 cash out = 4700
        closed.GrossCashSales.Should().Be(700m);
        closed.VoidedCashSales.Should().Be(500m);
        closed.RefundCashOut.Should().Be(0m);
        closed.CashIn.Should().Be(500m);
        closed.CashOut.Should().Be(1000m);
        closed.ExpectedCash.Should().Be(4700m);
        closed.ClosingCash.Should().Be(4700m);
        closed.CashDifference.Should().Be(0m);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Negosio.IntegrationTests --filter ReconciliationTests`
Expected: compile errors — `RegisterSessionDto` has no `GrossCashSales`/`VoidedCashSales`/`RefundCashOut`/`CashIn`/`CashOut` fields yet.

- [ ] **Step 3: Add the breakdown value object and `RegisterSession` columns**

Create `src/Negosio.Domain/Entities/Pos/CashReconciliationBreakdown.cs`:

```csharp
namespace Negosio.Domain.Entities;

/// <summary>The five components that sum to a closed session's expected cash, kept separately so the
/// close-session UI can render an unambiguous breakdown instead of one opaque total.</summary>
public sealed record CashReconciliationBreakdown(
    decimal GrossCashSales, decimal VoidedCashSales, decimal RefundCashOut, decimal CashIn, decimal CashOut);
```

In `src/Negosio.Domain/Entities/Pos/RegisterSession.cs`, add five properties after `CashDifference`:

```csharp
public decimal? GrossCashSales { get; private set; }

public decimal? VoidedCashSales { get; private set; }

public decimal? RefundCashOut { get; private set; }

public decimal? CashIn { get; private set; }

public decimal? CashOut { get; private set; }
```

Change `Close(...)`'s signature and body:

```csharp
public void Close(Guid closedByUserId, decimal closingCash, decimal expectedCash, CashReconciliationBreakdown breakdown)
{
    if (Status == RegisterSessionStatus.Closed)
    {
        throw new InvalidOperationException("This register session is already closed.");
    }

    if (closingCash < 0m)
    {
        throw new ArgumentOutOfRangeException(nameof(closingCash), "Closing cash cannot be negative.");
    }

    ClosedByUserId = closedByUserId;
    ClosingCash = closingCash;
    ExpectedCash = expectedCash;
    CashDifference = closingCash - expectedCash;
    GrossCashSales = breakdown.GrossCashSales;
    VoidedCashSales = breakdown.VoidedCashSales;
    RefundCashOut = breakdown.RefundCashOut;
    CashIn = breakdown.CashIn;
    CashOut = breakdown.CashOut;
    ClosedAtUtc = DateTime.UtcNow;
    Status = RegisterSessionStatus.Closed;
    Touch();
}
```

- [ ] **Step 4: Fix the existing unit tests for the new `Close(...)` signature**

In `tests/Negosio.UnitTests/Pos/RegisterSessionTests.cs`, update both existing calls to pass a breakdown:

```csharp
session.Close(Guid.NewGuid(), closingCash: 1180m, expectedCash: 1200m,
    new CashReconciliationBreakdown(1200m, 0m, 0m, 0m, 0m));
```

and in `Close_twice_throws`:

```csharp
session.Close(Guid.NewGuid(), 0m, 0m, new CashReconciliationBreakdown(0m, 0m, 0m, 0m, 0m));
```

Add `using Negosio.Domain.Entities;` if not already present (for `CashReconciliationBreakdown`). Add one new test confirming the breakdown round-trips:

```csharp
[Fact]
public void Close_stores_the_reconciliation_breakdown()
{
    var session = RegisterSession.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5000m);

    session.Close(Guid.NewGuid(), 4700m, 4700m, new CashReconciliationBreakdown(700m, 500m, 0m, 500m, 1000m));

    session.GrossCashSales.Should().Be(700m);
    session.VoidedCashSales.Should().Be(500m);
    session.RefundCashOut.Should().Be(0m);
    session.CashIn.Should().Be(500m);
    session.CashOut.Should().Be(1000m);
}
```

Run: `dotnet test tests/Negosio.UnitTests --filter RegisterSessionTests` — expected 5 passed (4 existing + 1 new) before moving on.

- [ ] **Step 5: Map the new columns and generate the migration**

In the register session EF configuration file (find it via `grep -rn "class RegisterSessionConfiguration"`), add after the existing `CashDifference` mapping:

```csharp
builder.Property(s => s.GrossCashSales).HasPrecision(18, 2);
builder.Property(s => s.VoidedCashSales).HasPrecision(18, 2);
builder.Property(s => s.RefundCashOut).HasPrecision(18, 2);
builder.Property(s => s.CashIn).HasPrecision(18, 2);
builder.Property(s => s.CashOut).HasPrecision(18, 2);
```

```bash
dotnet dotnet-ef migrations add AddRegisterSessionCashBreakdown --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
```

- [ ] **Step 6: Update `RegisterSessionDto` and `ReconcileAndCloseAsync`**

In `src/Negosio.Application/Registers/RegisterContracts.cs`, extend `RegisterSessionDto` with five trailing nullable fields (after `CashDifference`):

```csharp
public sealed record RegisterSessionDto(
    Guid Id,
    Guid BranchId,
    Guid RegisterId,
    string RegisterName,
    RegisterSessionStatus Status,
    Guid OpenedByUserId,
    string OpenedByName,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    decimal OpeningCash,
    decimal? ClosingCash,
    decimal? ExpectedCash,
    decimal? CashDifference,
    decimal? GrossCashSales,
    decimal? VoidedCashSales,
    decimal? RefundCashOut,
    decimal? CashIn,
    decimal? CashOut);
```

In `src/Negosio.Application/Registers/RegisterSessionService.cs`, rewrite `ReconcileAndCloseAsync`. **This method did not previously run inside an explicit transaction** — it needs one now, because `WITH (UPDLOCK, HOLDLOCK)` only holds the lock until the end of the *current transaction*; without one, SQL Server treats the lock-acquiring `SELECT` as its own auto-committed statement and releases the lock immediately afterward, before the reconciliation SUM queries even run — making the lock pointless. Wrap the whole body (lock through `SaveChangesAsync`) in `BeginTransactionAsync`/`CommitAsync`, matching the pattern already used in `CheckoutService`/`ReturnService`/`VoidSaleService`:

```csharp
private async Task<RegisterSessionDto> ReconcileAndCloseAsync(
    RegisterSession session, Guid tenantId, decimal closingCash, CancellationToken cancellationToken)
{
    if (session.Status != RegisterSessionStatus.Open)
    {
        throw new BusinessRuleException(ErrorCodes.RegisterSessionNotOpen, "This register session is not open.");
    }

    await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

    // Same pessimistic lock VoidSaleService.VoidAsync takes, taken here as the very first read too
    // — not just before the final write. Whoever gets here first (this close, or a concurrent void)
    // runs its entire read-then-write sequence to completion (commit or rollback, which releases the
    // lock) before the other can even begin reading, so these SUM queries below can never be a stale
    // snapshot relative to a void that commits moments later, or vice versa. HOLDLOCK's guarantee
    // depends entirely on this running inside the explicit transaction above.
    await _db.Database.SqlQuery<int>(
        $"SELECT 1 AS Value FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {session.Id}")
        .ToListAsync(cancellationToken);

    var saleIds = _db.Sales.Where(s => s.TenantId == tenantId && s.RegisterSessionId == session.Id).Select(s => s.Id);

    // Gross: every cash payment on this session's sales, regardless of a later void — Payment rows
    // are never deleted. Voided: the subset of that belonging to sales now Status == Voided, so the
    // UI can show "Gross" and "Voided" as two distinct lines rather than a pre-subtracted number.
    var grossCashSales = await _db.Payments
        .Where(p => p.TenantId == tenantId && p.Method == PaymentMethod.Cash && saleIds.Contains(p.SaleId))
        .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

    var voidedSaleIds = _db.Sales.Where(s => s.TenantId == tenantId && s.RegisterSessionId == session.Id && s.Status == SaleStatus.Voided).Select(s => s.Id);
    var voidedCashSales = await _db.Payments
        .Where(p => p.TenantId == tenantId && p.Method == PaymentMethod.Cash && voidedSaleIds.Contains(p.SaleId))
        .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

    var returnIds = _db.SaleReturns.Where(r => r.TenantId == tenantId && saleIds.Contains(r.SaleId)).Select(r => r.Id);
    var refundCashOut = await _db.RefundPayments
        .Where(r => r.TenantId == tenantId && r.Method == PaymentMethod.Cash && returnIds.Contains(r.SaleReturnId))
        .SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;

    var cashIn = await _db.RegisterCashMovements
        .Where(m => m.TenantId == tenantId && m.RegisterSessionId == session.Id && m.Type == CashMovementType.CashIn)
        .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;
    var cashOut = await _db.RegisterCashMovements
        .Where(m => m.TenantId == tenantId && m.RegisterSessionId == session.Id && m.Type == CashMovementType.CashOut)
        .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;

    var expected = session.OpeningCash + grossCashSales - voidedCashSales - refundCashOut + cashIn - cashOut;
    var breakdown = new CashReconciliationBreakdown(grossCashSales, voidedCashSales, refundCashOut, cashIn, cashOut);

    // ClosedByUserId = the acting user (the original cashier on a normal close, an Owner/Admin
    // on a force-close); OpenedByUserId is never touched.
    session.Close(_currentUser.UserId, closingCash, expected, breakdown);
    await _db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);

    return await ProjectAsync(session.Id, tenantId, cancellationToken);
}
```

Update `ProjectAsync`'s final `select new RegisterSessionDto(...)` to pass the five new fields (`s.GrossCashSales, s.VoidedCashSales, s.RefundCashOut, s.CashIn, s.CashOut`) after `s.CashDifference`.

- [ ] **Step 6b: Write and verify the void-vs-close concurrency test**

Create `tests/Negosio.IntegrationTests/Registers/CloseVoidConcurrencyTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Registers;

public class CloseVoidConcurrencyTests : IntegrationTest
{
    public CloseVoidConcurrencyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Concurrent_void_and_session_close_never_produce_an_inconsistent_reconciliation()
    {
        Client.DefaultRequestHeaders.Authorization = null;
        var owner = await RegisterLoginAndAuthorizeAsync();
        Client.DefaultRequestHeaders.Authorization = null;
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId, "R1", "R1");
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, quantity: 10m);

        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        var openRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/register-sessions/open", cashierToken,
            new OpenRegisterSessionRequest(register.Id, 1000m)));
        var session = (await openRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;

        var checkoutRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Post, "/api/pos/checkout", cashierToken,
            new CheckoutRequest(branchId, session.Id, Guid.NewGuid(),
                new[] { new CheckoutItemInput(variantId, 1m, null) },
                new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) })));
        var sale = (await checkoutRes.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;

        var voidTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/sales/{sale.SaleId}/void", owner.AccessToken,
            new VoidSaleRequest("Concurrent with close")));
        var closeTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/register-sessions/{session.Id}/close", cashierToken,
            new CloseRegisterSessionRequest(500m)));

        await Task.WhenAll(voidTask, closeTask);
        var voidRes = await voidTask;
        var closeRes = await closeTask;

        var finalSaleRes = await Client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/sales/{sale.SaleId}", owner.AccessToken));
        var finalSale = (await finalSaleRes.Content.ReadFromJsonAsync<SaleDetailDto>(TestJson.Options))!;

        if (voidRes.IsSuccessStatusCode)
        {
            // Void won — the session must still have been Open when its transaction committed.
            finalSale.Sale.Status.Should().Be(SaleStatus.Voided);
            if (closeRes.IsSuccessStatusCode)
            {
                // Close ran after void released the lock — its own numbers must reflect the void,
                // not a stale pre-void snapshot.
                var closedSession = (await closeRes.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
                closedSession.VoidedCashSales.Should().Be(500m);
                closedSession.ExpectedCash.Should().Be(1000m); // opening only — the one sale was voided
            }
        }
        else
        {
            // Close won — void must have been rejected because the session was already closed by
            // the time void's own (serialized) check ran.
            finalSale.Sale.Status.Should().Be(SaleStatus.Completed);
            closeRes.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
```

Run: `dotnet test tests/Negosio.IntegrationTests --filter CloseVoidConcurrencyTests`
Expected: 1 passed. As with B4's concurrency tests, treat any flakiness as a real bug in the locking, not something to retry past.

- [ ] **Step 7: Run to verify the test passes**

Run: `dotnet test tests/Negosio.IntegrationTests --filter ReconciliationTests`
Expected: 1 passed, with the exact decimal values asserted.

Then re-run the existing register/force-close tests to confirm nothing regressed:

Run: `dotnet test tests/Negosio.IntegrationTests --filter RegisterSessionModelTests`
Expected: all still passing (`Force_close_is_owner_admin_only_and_preserves_ownership`'s `CashDifference.Should().Be(0m)` assertion must still hold with the new formula — it has no sales/movements, so it degenerates to the old formula exactly).

- [ ] **Step 8: Commit**

```bash
git add src/Negosio.Domain/Entities/Pos/ \
        src/Negosio.Infrastructure/Persistence/Configurations/ \
        src/Negosio.Infrastructure/Persistence/Migrations/Tenant/ \
        src/Negosio.Application/Registers/ \
        tests/Negosio.UnitTests/Pos/RegisterSessionTests.cs \
        tests/Negosio.IntegrationTests/Registers/ReconciliationTests.cs \
        tests/Negosio.IntegrationTests/Registers/CloseVoidConcurrencyTests.cs
git commit -m "feat(registers): include cash movements and voids in reconciliation"
```

---

## Task F1: Frontend API types and client functions

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`
- Modify: `web/negosio-web/src/api/pos.ts` (`salesApi.void`, `sessionsApi.cashMovements*`)
- Modify: `web/negosio-web/src/api/staff.ts` (`staffApi.setSalesVoidPermission`)

**Interfaces:**
- Produces: `VoidSaleApprovalInput`, `VoidSaleRequest`, `SaleDetailDto` (extended), `ChangeStaffPermissionsRequest`, `StaffMemberDto` (extended), `RegisterCashMovementDto`, `CreateCashMovementRequest`, `RegisterSessionDto` (extended), `salesApi.void(id, body)`, `sessionsApi.cashMovements.create(sessionId, body)`, `sessionsApi.cashMovements.list(sessionId)`, `staffApi.setSalesVoidPermission(id, body)` — every later frontend task imports these exact names.

There is no runtime test for this task — TypeScript strict mode is the check. This task has no separate red/green cycle; it's pure setup consumed by F2-F5.

- [ ] **Step 1: Extend `web/negosio-web/src/api/types.ts`**

Find the existing `SaleDetailDto` interface/type and add the matching fields for the five new backend fields (keep the same casing convention already used elsewhere in this file — check whether the file uses camelCase field names matching JSON serialization, e.g. `saleNumber` not `SaleNumber`):

```ts
export interface SaleDetailDto {
  // ...existing fields...
  voidedByUserId: string | null
  voidedByName: string | null
  approvedByUserId: string | null
  approvedByName: string | null
  voidReason: string | null
  voidedAtUtc: string | null
  canVoid: boolean
  voidIneligibilityCode: string | null
}

export interface VoidSaleApprovalInput {
  approverEmail: string
  approverPassword: string
}

export interface VoidSaleRequest {
  reason: string
  approval?: VoidSaleApprovalInput
}
```

Add to `StaffMemberDto`:

```ts
export interface StaffMemberDto {
  // ...existing fields...
  salesVoid: boolean
}

export interface ChangeStaffPermissionsRequest {
  salesVoid: boolean
}
```

Add to `RegisterSessionDto` (find the existing interface, add after `cashDifference`):

```ts
export interface RegisterSessionDto {
  // ...existing fields...
  grossCashSales: number | null
  voidedCashSales: number | null
  refundCashOut: number | null
  cashIn: number | null
  cashOut: number | null
}

export type CashMovementType = 'CashIn' | 'CashOut'

export interface RegisterCashMovementDto {
  id: string
  type: CashMovementType
  amount: number
  reason: string
  createdByUserId: string
  createdByName: string
  createdAtUtc: string
}

export interface CreateCashMovementRequest {
  type: CashMovementType
  amount: number
  reason: string
}
```

Also confirm `SaleSummaryDto['status']` (or wherever `SaleStatus` is typed) already includes `'Voided'` as a literal — it should, since the backend enum already had it; just verify the TS union wasn't hand-trimmed to only `'Completed' | 'Refunded' | 'PartiallyRefunded'` somewhere.

- [ ] **Step 2: Extend `web/negosio-web/src/api/pos.ts`**

Add to `salesApi`:

```ts
export const salesApi = {
  // ...existing methods...
  void: (id: string, body: VoidSaleRequest) =>
    apiRequest<SaleDetailDto>(`/api/sales/${id}/void`, { method: 'POST', body }),
}
```

Add a nested `cashMovements` object to `sessionsApi`:

```ts
export const sessionsApi = {
  // ...existing methods...
  cashMovements: {
    create: (sessionId: string, body: CreateCashMovementRequest) =>
      apiRequest<RegisterCashMovementDto>(`/api/register-sessions/${sessionId}/cash-movements`, { method: 'POST', body }),
    list: (sessionId: string) =>
      apiRequest<RegisterCashMovementDto[]>(`/api/register-sessions/${sessionId}/cash-movements`),
  },
}
```

Add the new types to this file's `import type { ... } from './types'` list.

- [ ] **Step 3: Extend `web/negosio-web/src/api/staff.ts`**

```ts
export const staffApi = {
  // ...existing methods...
  setSalesVoidPermission: (id: string, body: ChangeStaffPermissionsRequest) =>
    apiRequest<StaffMemberDto>(`/api/staff/${id}/permissions`, { method: 'PUT', body }),
}
```

- [ ] **Step 4: Verify it compiles**

Run: `cd web/negosio-web && npx tsc --noEmit`
Expected: 0 errors (nothing consumes the new exports yet, so this only checks the type declarations themselves are well-formed).

- [ ] **Step 5: Commit**

```bash
cd web/negosio-web
git add src/api/types.ts src/api/pos.ts src/api/staff.ts
git commit -m "feat(web): add void/cash-movement/permission API types and client calls"
```

---

## Task F2: Void sale modal + Sale Detail wiring + Sales list badge

**Files:**
- Create: `web/negosio-web/src/components/sales/VoidSaleModal.tsx`
- Modify: `web/negosio-web/src/pages/SaleDetailPage.tsx`
- Modify: `web/negosio-web/src/components/sales/StatusBadge.tsx` (confirm/add a `'Voided'` case)
- Modify: `web/negosio-web/src/lib/useCan.ts` (add `'sales:void'` capability)

**Interfaces:**
- Consumes: `salesApi.void` (F1), `useCan` (existing), `Modal`/`Button`/`TextField`/`Callout`/`useToast` (existing `../ui`).
- Produces: `<VoidSaleModal open onClose sale onVoided />` — a single component covering both the direct-reason and approval sub-forms, switched on `sale.canVoid` staying true isn't the switch (that's button visibility) — the switch inside the modal is whether the *current user* needs approval, which the frontend cannot know in advance from the DTO (the DTO doesn't carry "does the viewer have a grant" — that's resolved server-side on submit). So the modal always starts in a single reason-only-looking form and reveals the approval fields only after the server responds `VOID_APPROVAL_REQUIRED` — see Step 1.

- [ ] **Step 1: Build `VoidSaleModal`**

Create `web/negosio-web/src/components/sales/VoidSaleModal.tsx`:

```tsx
import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { salesApi } from '../../api/pos'
import type { SaleDetailDto } from '../../api/types'
import { Button, Callout, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  sale: SaleDetailDto
  onVoided: (updated: SaleDetailDto) => void
}

const INELIGIBLE_MESSAGES: Record<string, string> = {
  SALE_NOT_VOIDABLE: 'This sale cannot be voided.',
  SALE_HAS_RETURNS: 'This sale has returns against it and cannot be voided.',
  VOID_SESSION_CLOSED:
    'This sale can no longer be voided because its register session has already been closed. Use the return/refund process instead.',
  VOID_CUTOFF_EXPIRED: 'This sale is past the void cutoff. Use the return/refund process instead.',
}

export function VoidSaleModal({ open, onClose, sale, onVoided }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [reason, setReason] = useState('')
  const [needsApproval, setNeedsApproval] = useState(false)
  const [approverEmail, setApproverEmail] = useState('')
  const [approverPassword, setApproverPassword] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setReason('')
    setNeedsApproval(false)
    setApproverEmail('')
    setApproverPassword('')
    setError('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () =>
      salesApi.void(sale.sale.id, {
        reason,
        approval: needsApproval ? { approverEmail, approverPassword } : undefined,
      }),
    onSuccess: (updated) => {
      qc.invalidateQueries({ queryKey: ['sales'] })
      qc.invalidateQueries({ queryKey: ['inventory'] })
      qc.invalidateQueries({ queryKey: ['session', 'current'] })
      toast('success', 'Sale voided')
      onVoided(updated)
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'VOID_APPROVAL_REQUIRED') {
        // First submit from a Cashier without the grant: reveal the approval fields, keep the reason.
        setNeedsApproval(true)
        setError('')
        return
      }
      if (err instanceof ApiError && err.code === 'INVALID_APPROVER_CREDENTIALS') {
        setError('Invalid manager credentials.')
        return
      }
      if (err instanceof ApiError && err.code === 'VOID_APPROVER_WRONG_BRANCH') {
        setError('This manager cannot approve voids for this branch.')
        return
      }
      if (err instanceof ApiError && err.code in INELIGIBLE_MESSAGES) {
        setError(INELIGIBLE_MESSAGES[err.code])
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not void this sale.')
    },
  })

  const canSubmit = reason.trim().length > 0 && (!needsApproval || (approverEmail.trim() && approverPassword))

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={`Void sale #${sale.sale.saleNumber}`}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            size="sm"
            variant="destructive"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!canSubmit}
          >
            {needsApproval ? 'Approve & void' : 'Void sale'}
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <p className="mb-4 text-[13px] text-text-muted">
        This will reverse the full sale and restore inventory.
      </p>

      {needsApproval && (
        <Callout tone="info">
          Manager approval required. You don&rsquo;t have permission to void completed sales — an
          authorized Manager, Admin, or Owner must approve this void.
        </Callout>
      )}

      {needsApproval && (
        <>
          <TextField
            label="Manager account"
            name="approverEmail"
            type="email"
            value={approverEmail}
            onChange={(e) => setApproverEmail(e.target.value)}
            autoFocus
          />
          <TextField
            label="Password"
            name="approverPassword"
            type="password"
            value={approverPassword}
            onChange={(e) => setApproverPassword(e.target.value)}
          />
        </>
      )}

      <TextField
        label="Reason"
        name="reason"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        autoFocus={!needsApproval}
      />
    </Modal>
  )
}
```

- [ ] **Step 2: Wire it into `SaleDetailPage.tsx`**

Add `const canVoidCapability = useCan('sales:void')` alongside the existing `canRefund`. Add `[voidOpen, setVoidOpen] = useState(false)`. In the action-buttons row (next to the existing `Start return`/`Print receipt` buttons), add — only when `canVoidCapability && d.sale.status === 'Completed'`:

```tsx
{canVoidCapability && d.sale.status === 'Completed' && (
  d.canVoid ? (
    <Button variant="destructive" size="sm" onClick={() => setVoidOpen(true)}>
      Void sale
    </Button>
  ) : null
)}
```

(`d.canVoid` already reflects the backend's own status/returns/session/cutoff order — no need to re-derive it client-side; when `false`, the button simply doesn't render, matching "hide/disable... backend remains authoritative.")

Change the existing `canStartReturn` condition to also require `d.sale.status !== 'Voided'` — it already implicitly excludes `Voided` via the `status === 'Completed' || status === 'PartiallyRefunded'` check, so no change is actually needed there; just double check that stays true after this task's other changes.

Render the modal near the existing `<ReturnModal .../>`:

```tsx
<VoidSaleModal
  open={voidOpen}
  onClose={() => setVoidOpen(false)}
  sale={d}
  onVoided={() => query.refetch()}
/>
```

Add a void-audit section, shown only when `d.sale.status === 'Voided'`, placed right under the header:

```tsx
{d.sale.status === 'Voided' && (
  <div className="rounded-xl border border-danger/20 bg-danger-light p-4 text-sm">
    <p className="font-semibold text-danger-strong">Voided</p>
    <dl className="mt-2 space-y-1 text-text-secondary">
      <div>
        <dt className="inline font-medium text-text-primary">Voided by: </dt>
        <dd className="inline">{d.voidedByName}</dd>
      </div>
      {d.approvedByName && (
        <div>
          <dt className="inline font-medium text-text-primary">Approved by: </dt>
          <dd className="inline">{d.approvedByName}</dd>
        </div>
      )}
      <div>
        <dt className="inline font-medium text-text-primary">Reason: </dt>
        <dd className="inline">{d.voidReason}</dd>
      </div>
      <div>
        <dt className="inline font-medium text-text-primary">Voided at: </dt>
        <dd className="inline">{d.voidedAtUtc ? new Date(d.voidedAtUtc).toLocaleString() : ''}</dd>
      </div>
    </dl>
  </div>
)}
```

Import `VoidSaleModal` and `useCan` at the top of the file.

- [ ] **Step 3: Confirm `StatusBadge` covers `Voided`**

Open `web/negosio-web/src/components/sales/StatusBadge.tsx`. If it's a `Record<SaleStatus, {label, className}>`-style lookup missing a `Voided` entry, add one (danger-toned, matching the void-audit box's palette above):

```ts
Voided: { label: 'Voided', className: 'bg-danger-light text-danger-strong' },
```

- [ ] **Step 4: Add the `sales:void` capability**

In `web/negosio-web/src/lib/useCan.ts`, add `'sales:void'` to the `Capability` union and to `CAPABILITY_ROLES`:

```ts
// Mirrors SalesController's class-level SalesView policy (Owner/Admin/Manager/Cashier) — the
// Cashier direct-vs-approval split is resolved server-side per sale, not by this capability.
'sales:void': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
```

- [ ] **Step 5: Verify**

Run: `cd web/negosio-web && npm run lint && npx tsc --noEmit`
Expected: both clean.

- [ ] **Step 6: Commit**

```bash
cd web/negosio-web
git add src/components/sales/VoidSaleModal.tsx src/pages/SaleDetailPage.tsx \
        src/components/sales/StatusBadge.tsx src/lib/useCan.ts
git commit -m "feat(web): add void sale workflow and approval modal"
```

---

## Task F3: Staff permission toggle + branch-scoped Staff view

**Files:**
- Create: `web/negosio-web/src/components/staff/SalesVoidPermissionToggle.tsx`
- Modify: `web/negosio-web/src/pages/StaffPage.tsx`
- Modify: `web/negosio-web/src/lib/useCan.ts` (add `'staff:permissions'`)
- Modify: `web/negosio-web/src/lib/nav.ts` (Staff route reachable on either `staff:manage` or `staff:permissions`)

**Interfaces:**
- Consumes: `staffApi.setSalesVoidPermission` (F1), `useCan` (existing).
- Produces: `<SalesVoidPermissionToggle member onChanged />` — a small inline control (not a full modal, per the spec's "no generic complex permissions editor" instruction), rendered only on Cashier rows.

- [ ] **Step 1: Build `SalesVoidPermissionToggle`**

Create `web/negosio-web/src/components/staff/SalesVoidPermissionToggle.tsx`:

```tsx
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { staffApi } from '../../api/staff'
import type { StaffMemberDto } from '../../api/types'
import { useToast } from '../ui'

interface Props {
  member: StaffMemberDto
}

/** Inline toggle for a Cashier row's "Allow voiding completed sales" permission — deliberately not
 * a modal, per the phase's scope: this is the only editable field, not a generic permissions editor. */
export function SalesVoidPermissionToggle({ member }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const mutation = useMutation({
    mutationFn: (next: boolean) => staffApi.setSalesVoidPermission(member.id, { salesVoid: next }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast('success', 'Permission updated')
    },
    onError: (err) => toast('error', err instanceof ApiError ? err.message : 'Could not update this permission.'),
  })

  return (
    <label className="flex items-center gap-1.5 text-[13px] text-text-secondary">
      <input
        type="checkbox"
        checked={member.salesVoid}
        disabled={mutation.isPending}
        onChange={(e) => mutation.mutate(e.target.checked)}
        className="size-4 rounded border-border-strong text-primary-600 focus-visible:outline-2 focus-visible:outline-primary-500"
      />
      Allow void
    </label>
  )
}
```

- [ ] **Step 2: Wire it into `StaffPage.tsx`**

Add `const canManagePermissions = useCan('staff:permissions')` alongside the existing capability checks. In the Actions cell (`Table.Cell align="right"`), inside the `canManage(m)` branch, before the existing action buttons, add — only for a Cashier row and only when the viewer may manage it:

```tsx
{m.kind === 'Member' && m.role === 'Cashier' && canManagePermissions && (
  <SalesVoidPermissionToggle member={m} />
)}
```

A Manager's own capability check happens server-side (branch mismatch → 403, surfaced via the mutation's `onError` toast above) — no client-side branch filtering is needed here since `GET /api/staff` (B2) already returns only that Manager's own branch.

For a Manager viewer specifically, hide the other action buttons entirely (Invite/Change role/Change branch/Deactivate/Reactivate stay Owner/Admin-only per the locked spec) — gate the existing `canManage(m)` block's *other* buttons behind `useCan('staff:manage')` (not `staff:permissions`), so a Manager sees only the permission toggle on Cashier rows and nothing else:

```tsx
{canManageFull ? (
  <>
    {/* existing Change role / Change branch / Deactivate / Reactivate buttons, unchanged */}
  </>
) : null}
```

where `canManageFull = useCan('staff:manage')`. Also hide the page-level `Invite staff` button and the `EmptyState`'s invite action behind `canManageFull` (currently unconditional).

- [ ] **Step 3: Add `staff:permissions` and update the nav gate**

In `web/negosio-web/src/lib/useCan.ts`:

```ts
// Mirrors StaffController's new StaffView/StaffPermissionManage overrides (Owner/Admin/Manager) —
// distinct from 'staff:manage' (Owner/Admin only), which still gates every other staff action.
'staff:permissions': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
```

In `web/negosio-web/src/lib/nav.ts`, the Staff item currently has a single `capability: 'staff:manage'`. Since `NavItem.capability` is a single value, change the nav-rendering check (wherever `NAV_GROUPS` is consumed, likely `Sidebar.tsx` or similar — locate via `grep -rn "capability" web/negosio-web/src/components` outside `nav.ts`/`useCan.ts` itself) to treat a Staff-specific case as "either capability," OR simpler: change `NavItem.capability` to optionally accept an array and have the nav renderer show the item if *any* listed capability is held. Prefer the array approach since it's a small, general improvement rather than a one-off special case:

```ts
export interface NavItem {
  // ...
  capability?: Capability | Capability[]
}
```

Update the Staff entry:

```ts
{ label: 'Staff', icon: Users, to: '/staff', enabled: true, capability: ['staff:manage', 'staff:permissions'] },
```

Update the nav renderer's check from `capabilities(item.capability)` to something that treats a single value and an array uniformly, e.g.:

```ts
const allowed = !item.capability || (Array.isArray(item.capability) ? item.capability.some(capabilities) : capabilities(item.capability))
```

- [ ] **Step 4: Verify**

Run: `cd web/negosio-web && npm run lint && npx tsc --noEmit`
Expected: both clean.

- [ ] **Step 5: Commit**

```bash
cd web/negosio-web
git add src/components/staff/SalesVoidPermissionToggle.tsx src/pages/StaffPage.tsx \
        src/lib/useCan.ts src/lib/nav.ts
git commit -m "feat(web): add cashier void permission management"
```

---

## Task F4: Cash In / Cash Out session controls

**Files:**
- Create: `web/negosio-web/src/components/pos/CashMovementModal.tsx`
- Modify: `web/negosio-web/src/components/pos/PosShell.tsx` (or wherever the `Close session` button currently renders inside the POS shell — inspect it first, it was not read in research)
- Modify: `web/negosio-web/src/lib/useCan.ts` (add `'register:cash-movement'`)

**Interfaces:**
- Consumes: `sessionsApi.cashMovements.create` (F1), `useCan` (existing).
- Produces: `<CashMovementModal open onClose type sessionId onDone />` — one component parameterized by `type: 'CashIn' | 'CashOut'` rather than two near-identical components (DRY).

Scope note, carried from the spec: this task adds the ability to *create* movements from the POS session controls. It does **not** add a dedicated cash-movement ledger/list page — Manager/Owner/Admin "viewing" movements is satisfied by the reconciliation breakdown (F5), not a separate raw list UI; the `sessionsApi.cashMovements.list` client function from F1 exists for that breakdown to use if convenient, but building a standalone viewer is out of scope (YAGNI — the spec never asked for one).

- [ ] **Step 1: Read `PosShell.tsx` first**

Open `web/negosio-web/src/components/pos/PosShell.tsx` to find exactly where and how the existing `Close session` button/callback (`onCloseSession`, passed from `PosPage.tsx`) is rendered, and what session-summary info (opening cash, register name) is already available in that component's props — reuse the same prop-drilling shape for the two new buttons rather than inventing a new one.

- [ ] **Step 2: Build `CashMovementModal`**

Create `web/negosio-web/src/components/pos/CashMovementModal.tsx`:

```tsx
import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { sessionsApi } from '../../api/pos'
import type { CashMovementType } from '../../api/types'
import { Button, Callout, Modal, TextField } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  sessionId: string
  type: CashMovementType
  onDone: () => void
}

const COPY: Record<CashMovementType, { title: string; cta: string; placeholder: string }> = {
  CashIn: { title: 'Cash in', cta: 'Add cash', placeholder: 'Additional float' },
  CashOut: { title: 'Cash out', cta: 'Remove cash', placeholder: 'Petty cash' },
}

export function CashMovementModal({ open, onClose, sessionId, type, onDone }: Props) {
  const qc = useQueryClient()
  const [amount, setAmount] = useState('')
  const [reason, setReason] = useState('')
  const [error, setError] = useState('')
  const copy = COPY[type]

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setAmount('')
    setReason('')
    setError('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () => sessionsApi.cashMovements.create(sessionId, { type, amount: Number(amount), reason }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['session', 'current'] })
      onDone()
      onClose()
    },
    onError: (err) => setError(err instanceof ApiError ? err.message : 'Could not record this movement.'),
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    const value = Number(amount)
    if (!(value > 0)) {
      setError('Enter an amount greater than zero.')
      return
    }
    if (!reason.trim()) {
      setError('A reason is required.')
      return
    }
    setError('')
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={copy.title}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            {copy.cta}
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}
      <form onSubmit={submit}>
        <TextField
          label="Amount"
          name="amount"
          type="number"
          min={0.01}
          step="0.01"
          value={amount}
          onChange={(e) => setAmount(e.target.value)}
          autoFocus
        />
        <TextField
          label="Reason"
          name="reason"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          placeholder={copy.placeholder}
        />
      </form>
    </Modal>
  )
}
```

- [ ] **Step 3: Wire two buttons + the modal into `PosShell.tsx`**

Based on Step 1's findings, add `Cash in` / `Cash out` `Button`s (ghost or secondary variant, matching whatever the existing `Close session` button's variant is) next to it, gated by `useCan('register:cash-movement')`. Add local `useState` for which movement type's modal is open (or two booleans), and render one `<CashMovementModal>` reused for both types (switching `type` and `open` based on which button was clicked) — mirrors the `CloseSessionModal`'s `variant` prop pattern already used elsewhere in this codebase.

- [ ] **Step 4: Add the capability**

In `web/negosio-web/src/lib/useCan.ts`:

```ts
// Mirrors RegisterSessionsController's class-level PosOperate policy — ownership of the specific
// session (not just role) is enforced server-side by RegisterCashMovementService.
'register:cash-movement': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
```

- [ ] **Step 5: Verify**

Run: `cd web/negosio-web && npm run lint && npx tsc --noEmit`
Expected: both clean.

- [ ] **Step 6: Commit**

```bash
cd web/negosio-web
git add src/components/pos/CashMovementModal.tsx src/components/pos/PosShell.tsx src/lib/useCan.ts
git commit -m "feat(web): add cash in/out session controls"
```

---

## Task F5: Close Session reconciliation breakdown

**Files:**
- Modify: `web/negosio-web/src/components/pos/CloseSessionModal.tsx`

**Interfaces:**
- Consumes: the five new `RegisterSessionDto` fields (F1/B5), already returned by the existing `sessionsApi.close`/`forceClose` calls this component uses — no new API call needed.

- [ ] **Step 1: Extend the result breakdown `<dl>`**

In `CloseSessionModal.tsx`'s `result` branch, replace the existing four-row `<dl>` with the full breakdown, rendered only when the new fields are present (they always will be after B5 ships, but guard with `?? null` checks the same way `result.expectedCash ?? 0` already does, in case an older cached response shape ever flows through):

```tsx
<dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 rounded-lg bg-surface-subtle px-3 py-3 text-sm">
  <dt className="text-text-muted">Opening cash</dt>
  <dd className="text-right text-text-secondary">{formatMoney(result.openingCash)}</dd>

  {result.grossCashSales != null && (
    <>
      <dt className="text-text-muted">Gross cash sales</dt>
      <dd className="text-right text-text-secondary">{formatMoney(result.grossCashSales)}</dd>
    </>
  )}
  {result.voidedCashSales != null && result.voidedCashSales > 0 && (
    <>
      <dt className="text-text-muted">Voided cash sales</dt>
      <dd className="text-right text-danger-strong">−{formatMoney(result.voidedCashSales)}</dd>
    </>
  )}
  {result.grossCashSales != null && result.voidedCashSales != null && (
    <>
      <dt className="font-medium text-text-secondary">Net cash sales</dt>
      <dd className="text-right font-medium text-text-secondary">
        {formatMoney(result.grossCashSales - result.voidedCashSales)}
      </dd>
    </>
  )}
  {result.refundCashOut != null && result.refundCashOut > 0 && (
    <>
      <dt className="text-text-muted">Refund cash out</dt>
      <dd className="text-right text-danger-strong">−{formatMoney(result.refundCashOut)}</dd>
    </>
  )}
  {result.cashIn != null && result.cashIn > 0 && (
    <>
      <dt className="text-text-muted">Cash in</dt>
      <dd className="text-right text-text-secondary">{formatMoney(result.cashIn)}</dd>
    </>
  )}
  {result.cashOut != null && result.cashOut > 0 && (
    <>
      <dt className="text-text-muted">Cash out</dt>
      <dd className="text-right text-danger-strong">−{formatMoney(result.cashOut)}</dd>
    </>
  )}

  <dt className="pt-1 font-semibold text-text-primary">Expected cash</dt>
  <dd className="pt-1 text-right font-semibold text-text-primary">{formatMoney(result.expectedCash ?? 0)}</dd>
  <dt className="text-text-muted">Counted</dt>
  <dd className="text-right text-text-secondary">{formatMoney(result.closingCash ?? 0)}</dd>
  <dt className="pt-1 font-semibold text-text-primary">Difference</dt>
  <dd className={cn('pt-1 text-right font-semibold', over ? 'text-success-strong' : 'text-danger-strong')}>
    {formatMoney(difference)} {over ? 'over' : 'short'}
  </dd>
</dl>
```

("Net cash sales" is display-only arithmetic (`gross - voided`), never a separately stored/summed number — matches the spec's "nothing is subtracted twice" requirement.)

- [ ] **Step 2: Verify**

Run: `cd web/negosio-web && npm run lint && npx tsc --noEmit`
Expected: both clean.

Run: `cd web/negosio-web && npm run build`
Expected: clean production build.

- [ ] **Step 3: Commit**

```bash
cd web/negosio-web
git add src/components/pos/CloseSessionModal.tsx
git commit -m "feat(web): show void and cash-movement breakdown in session close"
```

---

## Task V1: Full backend + frontend verification

**Files:** none — this task runs the full test/build suite and fixes anything it finds. If a fix is needed, it belongs in whichever earlier task's files it touches (amend that task's commit conceptually by making a small follow-up commit here referencing it — do not weaken an assertion to make it pass).

- [ ] **Step 1: Kill stale processes**

Kill any running `Negosio.Api`/`vite` process first (repo convention — LocalDB file locks otherwise).

- [ ] **Step 2: Backend build**

Run: `dotnet build Negosio.sln`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Backend tests**

Run: `dotnet test Negosio.sln`
Expected: all unit + integration tests green. Record the exact final counts (unit / integration / failed / skipped) — the Phase 5 baseline was 88 unit / 162 integration, 0 failed; this phase adds roughly 6 (B1, unit) + 5 (B2) + 4 (B3) + 13 (B4: 11 `VoidSaleTests` + 2 `VoidConcurrencyTests`) + 2 (B5: 1 `ReconciliationTests` + 1 `CloseVoidConcurrencyTests`, plus a new unit test in the existing `RegisterSessionTests`) = ~30 new tests across unit + integration, so expect the totals to rise by roughly that much — report the actual numbers, not this estimate.

- [ ] **Step 4: Frontend lint**

Run: `cd web/negosio-web && npm run lint`
Expected: clean.

- [ ] **Step 5: Frontend build**

Run: `cd web/negosio-web && npm run build`
Expected: clean production build. Note the bundle size (compare against the Phase 5 baseline: 474.67 kB js / 132.29 kB gz) in the final report.

- [ ] **Step 6: Fix anything red**

If any step fails, use `superpowers:systematic-debugging` before patching — do not guess. Once fixed, re-run Steps 2-5 until all are green, then commit the fix with a message describing what was actually wrong (not "fix tests").

---

## Task V2: Browser verification

Use the real API + frontend (not mocks) — follow this session's `visual-verify-headless-chrome.md` memory recipe (headless Chrome + CDP) if no interactive browser is available. Seed at minimum four accounts in one tenant: Owner, a BGC-branch Manager, a BGC Cashier with the `SalesVoid` grant, and a BGC Cashier without it. Execute every scenario below for real and record what actually happened — do not claim a scenario passed without having run it.

- [ ] **Scenario 1 — Cashier without permission:** log in as the ungranted Cashier, open a Sale Detail, click Void. Expect the approval form (Manager account / Password / Reason, no reason-only shortcut). Enter valid BGC-Manager credentials → void succeeds, status `Voided`, `Voided by` = Cashier, `Approved by` = Manager, inventory restored.
- [ ] **Scenario 2 — Invalid approval:** repeat with a wrong password. Expect a friendly generic error, the sale unchanged, the modal still usable, no console errors.
- [ ] **Scenario 3 — Cashier with permission:** from Staff, grant `SalesVoid` to the Cashier. On a different eligible sale, click Void — expect reason-only, no Manager account/password fields; void succeeds.
- [ ] **Scenario 4 — Permission revoke:** revoke the grant. Without re-login, the same Cashier opens another sale and clicks Void — expect the approval form again (immediate effect, no JWT wait).
- [ ] **Scenario 5 — Manager permission management:** as the BGC Manager, go to Staff, confirm only BGC staff are listed, only the permission toggle is visible/active on Cashier rows (no Invite/role/branch/deactivate), and toggling a BGC Cashier's permission works while a MAIN Cashier is not reachable from this view at all.
- [ ] **Scenario 6 — Voided Sale UI:** confirm the `VOIDED` badge, unchanged sale number, void reason, voided-by, approved-by (only when set), the Void button gone, and `Start Return` gone; open the receipt and confirm it also reads `VOIDED`.
- [ ] **Scenario 7 — Inventory reversal:** note a variant's on-hand quantity before a sale, after the sale, and after voiding it — confirm it returns to the pre-sale figure, and that the same variant in a different branch is untouched.
- [ ] **Scenario 8 — Cash In:** as a Cashier on their own open session, use Cash In with an amount and reason; confirm it's reflected the next time the session's numbers are shown (e.g. on close).
- [ ] **Scenario 9 — Cash Out:** same, for Cash Out, confirming the expected-cash direction is a decrease.
- [ ] **Scenario 10 — Close Session reconciliation:** on one session, produce an opening cash, a cash sale, a void of that sale, a Cash In, and a Cash Out; open Close Session and confirm every breakdown line and the final Expected/Counted/Difference math is exactly right; enter counted cash and close; confirm the persisted session shows the same numbers afterward.
- [ ] **Scenario 11 — RBAC matrix:** confirm Owner and Admin get direct void on any branch, Manager gets direct void only on their own branch, a granted Cashier gets direct void, an ungranted Cashier gets the approval flow, and InventoryStaff/Viewer/KitchenStaff see no Void action at all.
- [ ] **Scenario 12 — Branch boundary:** confirm a BGC Manager/Cashier cannot void a MAIN sale (and cannot even see it, per the 404-not-403 pattern), while Owner/Admin can act on either branch — with no regression to any other Phase 5 branch behavior.
- [ ] **Regression smoke:** walk `/dashboard`, `/categories`, `/products`, `/inventory`, `/inventory/movements`, `/registers`, `/pos`, `/sales`, `/settings`, `/staff`, `/branches` as Owner, and confirm the Cashier role still cannot reach `/registers`, `/branches`, `/settings`, or the full `/staff` write actions (only the new permission toggle, per Scenario 5's Manager case — a Cashier should not reach `/staff` at all, since neither `staff:manage` nor `staff:permissions` includes `Cashier`). Note any console errors seen during the whole pass.

Record every scenario's actual outcome (not a prediction) for the final report.

---

## Final report

After V1 and V2 are both green, produce the 9-section final report the user's brief asked for (Architecture, Backend, Frontend, Security, Financial Correctness, Tests, Browser Verification, Git State, Remaining Limitations) — mirroring the structure of `docs/phase-5-status.md` from the previous phase. Save it as `docs/phase-6-status.md`. Explicitly preserve in the "Remaining Limitations" section: no partial void, no line-level post-sale void, no multiple branch assignments, no stock transfers, no branch-specific tax/pricing/catalog, no consolidated reporting, no Reporting phase, no offline selling, no F&B, no tenant timezone, PHP currency fixed, one email = one tenant, no separate cashier PIN, no production invitation email provider if still absent — plus this phase's own new ones: no closed-session void (same-day, session-open only), no permission grant/revoke history, no standalone cash-movement ledger viewer.

**Then STOP. Do NOT merge `feature/void-cash-operations` into `master`.** Leave the branch clean and ready for review, and tell the user it's ready.
