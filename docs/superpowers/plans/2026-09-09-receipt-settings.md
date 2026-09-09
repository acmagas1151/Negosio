# Receipt Settings Implementation Plan (Plan A of 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Owner/Admin/Manager configure a receipt header, footer, business-info toggles and thermal width — stored server-side as a tenant-default row plus optional per-branch override rows — and make the existing Sales Receipt render from that configuration with safe fallback to today's exact output.

**Architecture:** New `ReceiptSettings` aggregate in the tenant DB (one row per scope; a branch row is a **complete** snapshot, never a field-merge). A scoped `IReceiptSettingsResolver` returns the first existing row (branch → tenant-default → hardcoded default). `ReceiptService` joins the resolved settings + business identity (moved onto `TenantProfile`/`Branch`) into an extended `ReceiptDto`; the React `ReceiptPage` renders it through new shared `ReceiptHeader`/`ReceiptFooter`/`ReceiptBusinessInfo` components. A new `/settings/receipts` page (under a small Settings tab shell) edits the rows, with a client-only live preview.

**Tech Stack:** .NET 9, EF Core 9 (SQL Server, `TenantDbContext`), FluentValidation, xUnit + FluentAssertions (`Negosio.UnitTests`, `Negosio.IntegrationTests` via `NegosioApiFactory`). React 19 + TypeScript strict, Vite, React Router 7, TanStack Query v5, Tailwind v4.

**Spec:** `docs/superpowers/specs/2026-09-09-receipt-settings-design.md` (APPROVED 2026-09-09). Read it alongside this plan.

**Branch:** `feature/receipt-settings` (already created, design spec committed as `c908d2c`).

## Global Constraints

- **Currency is PHP**, timezone is fixed **Asia/Manila (UTC+8)** — not relevant to stored settings but relevant to any displayed "last updated" timestamp (render via existing helpers, do not reinvent).
- **All authoritative values stay server-side.** The receipt front end never recomputes a total; it renders `ReceiptDto` fields verbatim.
- **Plain text only** for header/footer. Never `dangerouslySetInnerHTML`, never interpret markdown/HTML. Escape on render.
- **Whole-row snapshot resolution.** No nullable/tri-state booleans for inheritance. A `ReceiptSettings` row is always complete. Resolution = first existing row wins: branch row → tenant-default row → in-code `HARDCODED_DEFAULT`.
- **Fallback is mandatory.** A tenant with zero `ReceiptSettings` rows must get byte-identical receipt output to today. No seed row, no data migration that blanks anything.
- **Policy `ReceiptSettingsManage` = Owner + Admin + Manager.** Manager is branch-scoped **server-side** via `IBranchAccessResolver` (a Manager can only write their own branch's row and may not write the tenant default). Hiding the nav item is cosmetic only.
- **Audit:** every `ReceiptSettings` write stamps `UpdatedByUserId` (+ `UpdatedAtUtc` via `Entity.Touch()` / `TenantDbContext.TouchTimestamps`).
- **Text limits:** header/footer ≤ 500 chars each, trimmed, internal whitespace runs collapsed, control chars stripped, newlines kept (≤ 6 lines). `ContactNumber` / `TaxId` ≤ 40. Server-side FluentValidation is the source of truth.
- **EF migration** (tenant context only): `dotnet dotnet-ef migrations add <Name> --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb`. All new columns/tables additive & nullable.
- **Do not merge, do not push.** Commit frequently on `feature/receipt-settings`.
- **Frontend gates:** `npx tsc --noEmit` and `npm run build` must stay clean (run from `web/negosio-web`). `useCan` capability sets must mirror the backend policy exactly (there are in-file comments enforcing this — update them).
- **Test commands:** backend `dotnet test` (from repo root); a single class `dotnet test --filter "FullyQualifiedName~ReceiptSettingsTests"`. Full suite is currently **320 green** — keep it green.

---

## File Structure

**Backend — new**
| File | Responsibility |
|---|---|
| `src/Negosio.Domain/Enums/ReceiptWidth.cs` | `enum ReceiptWidth { Mm80 = 0, Mm58 = 1 }` |
| `src/Negosio.Domain/Entities/Settings/ReceiptSettings.cs` | The aggregate. Factory `CreateDefault`, `Update(ReceiptSettingsValues, Guid userId)`. All bools non-nullable. |
| `src/Negosio.Application/Settings/ReceiptSettingsValues.cs` | Plain record carrying every presentation field + `static ReceiptSettingsValues HardcodedDefault`. Shared by entity, resolver, DTO mapping. |
| `src/Negosio.Application/Settings/ReceiptSettingsContracts.cs` | `ReceiptSettingsDto`, `ReceiptSettingsScope` enum, `UpdateReceiptSettingsRequest`, `IReceiptSettingsService`. |
| `src/Negosio.Application/Settings/ReceiptSettingsService.cs` | Get / Update / Reset. Role already checked by policy; branch scoping via `IBranchAccessResolver`; seed-on-first-write. |
| `src/Negosio.Application/Settings/ReceiptSettingsValidators.cs` | `UpdateReceiptSettingsRequestValidator` + a shared `ReceiptText.Normalize(string?)` helper. |
| `src/Negosio.Application/Settings/IReceiptSettingsResolver.cs` + `ReceiptSettingsResolver.cs` | Scoped, request-memoized. `Task<ReceiptSettingsValues> ResolveAsync(Guid branchId, CancellationToken)`. |
| `src/Negosio.Infrastructure/Persistence/Configurations/ReceiptSettingsConfiguration.cs` | Table `ReceiptSettings`, filtered unique indexes. |
| `src/Negosio.Api/Controllers/ReceiptSettingsController.cs` | `GET/PUT/DELETE /api/settings/receipts`. |
| `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/*_AddReceiptSettingsAndBusinessInfo.cs` | Generated migration. |

**Backend — modified**
| File | Change |
|---|---|
| `src/Negosio.Domain/Entities/Tenant/TenantProfile.cs` | `+ string? ContactNumber`, `+ string? TaxId`, `ConfigureBusinessInfo(...)`. |
| `src/Negosio.Domain/Entities/Branch.cs` | `+ string? ContactNumber` threaded through `Create` / `UpdateDetails`. |
| `src/Negosio.Infrastructure/Persistence/Configurations/TenantProfileConfiguration.cs` | 2 columns, `HasMaxLength(40)`. |
| `src/Negosio.Infrastructure/Persistence/Configurations/BranchConfiguration.cs` | 1 column, `HasMaxLength(40)`. |
| `src/Negosio.Application/Branches/BranchContracts.cs` | `BranchDto` / `CreateBranchRequest` / `UpdateBranchRequest` gain `ContactNumber`. |
| `src/Negosio.Application/Branches/BranchManagementService.cs` + `BranchQueryService.cs` | Projection + `Create`/`Update` pass `ContactNumber`. |
| `src/Negosio.Application/Branches/BranchValidators.cs` | `ContactNumber` ≤ 40, optional. |
| `src/Negosio.Application/Settings/TenantSettingsService.cs` | Add `GetBusinessInfoAsync` / `UpdateBusinessInfoAsync` (or fold into a `SettingsController` `business-info` endpoint). |
| `src/Negosio.Api/Controllers/SettingsController.cs` | `GET/PUT /api/settings/business-info` (policy `TenantSettingsWrite`). |
| `src/Negosio.Application/Sales/SaleContracts.cs` | `ReceiptDto` + `ReceiptPaymentDto` extensions (§ Task 8). |
| `src/Negosio.Application/Sales/ReceiptService.cs` | Join settings + identity; populate new fields. |
| `src/Negosio.Api/Authorization/AuthorizationPolicies.cs` | `ReceiptSettingsManage` const + policy (Owner/Admin/Manager). |
| `src/Negosio.Application/DependencyInjection.cs` | Register `IReceiptSettingsService`, `IReceiptSettingsResolver`. |
| `src/Negosio.Application/Abstractions/ITenantDbContext.cs` + `.../Tenant/TenantDbContext.cs` | `DbSet<ReceiptSettings> ReceiptSettings`. |
| `src/Negosio.Domain/Enums/DocumentNumberType.cs` | `DeliveryReceipt = 5` (added here so Plan B needs no domain change; unused until Plan B). |

**Frontend — new**
| File | Responsibility |
|---|---|
| `web/negosio-web/src/api/receiptSettings.ts` | `receiptSettingsApi.get(branchId?)`, `.update(branchId, body)`, `.reset(branchId)`. |
| `web/negosio-web/src/hooks/useReceiptSettings.ts` | Query hook + `RECEIPT_SETTINGS_QUERY_KEY`. |
| `web/negosio-web/src/components/receipt/ReceiptBusinessInfo.tsx` | Name / branch / address / contact / TIN lines. |
| `web/negosio-web/src/components/receipt/ReceiptHeader.tsx` | Custom header text **or** `ReceiptBusinessInfo`, per props. |
| `web/negosio-web/src/components/receipt/ReceiptFooter.tsx` | Footer text, multi-line, escaped. |
| `web/negosio-web/src/components/receipt/receiptStyles.ts` | `thermalReceiptCss(width: 'Mm80'|'Mm58'): string`. |
| `web/negosio-web/src/components/ui/TextArea.tsx` | Textarea matching `TextField` styling; exported from `ui/index.ts`. |
| `web/negosio-web/src/components/settings/SettingsTabs.tsx` | Small tab nav (`Tax` / `Receipts`) driven by route. |
| `web/negosio-web/src/pages/settings/ReceiptSettingsPage.tsx` | The page: General / Sales receipt / Delivery receipt sub-tabs + live preview + Save/Discard/Reset. |
| `web/negosio-web/src/components/settings/ReceiptPreview.tsx` | Client-only preview from unsaved form state, reusing `ReceiptHeader`/`ReceiptFooter`. |

**Frontend — modified**
| File | Change |
|---|---|
| `web/negosio-web/src/api/types.ts` | `ReceiptSettingsDto`, `UpdateReceiptSettingsRequest`, `ReceiptWidth`, extended `ReceiptDto`/receipt payment shape, `Branch*` `contactNumber`. |
| `web/negosio-web/src/pages/ReceiptPage.tsx` | Render via shared components + width + new payment rows. |
| `web/negosio-web/src/pages/SettingsPage.tsx` | Becomes a thin wrapper rendering `SettingsTabs` + the Tax card at `/settings/tax`. |
| `web/negosio-web/src/App.tsx` | Routes `/settings` → redirect `/settings/tax`; `/settings/tax`; `/settings/receipts` (guarded `receipt:settings`). |
| `web/negosio-web/src/lib/nav.ts` | `Settings` item stays; note it now routes to `/settings/tax`. (Receipt Settings reached via the in-page tab; no separate nav row — keeps nav uncluttered. See Task 12 note.) |
| `web/negosio-web/src/lib/useCan.ts` | `'receipt:settings'` capability = `Owner/Admin/Manager`. |
| `web/negosio-web/src/pages/BranchesPage.tsx` (+ branch form component) | `contactNumber` field. |

---

## Task 1: `ReceiptWidth` enum + `ReceiptSettingsValues` record + hardcoded default

**Files:**
- Create: `src/Negosio.Domain/Enums/ReceiptWidth.cs`
- Create: `src/Negosio.Application/Settings/ReceiptSettingsValues.cs`
- Test: `tests/Negosio.UnitTests/Settings/ReceiptSettingsValuesTests.cs`

**Interfaces:**
- Produces:
  - `enum ReceiptWidth { Mm80 = 0, Mm58 = 1 }`
  - `sealed record ReceiptSettingsValues(ReceiptWidth Width, string? SalesHeaderText, string? SalesFooterText, bool SalesShowBranch, bool SalesShowCashier, bool SalesShowPaymentMethod, bool SalesShowTaxLine, bool SalesShowReferenceNumber, string? DeliveryHeaderText, string? DeliveryFooterText, bool DeliveryShowPrices, bool DeliveryShowRelatedSaleNumber, bool DeliveryShowContactNumber, bool DeliveryShowSignatureFields)`
  - `static ReceiptSettingsValues ReceiptSettingsValues.HardcodedDefault { get; }` — `Width = Mm80`, both header/footer `null`, **every bool `true`**.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class ReceiptSettingsValuesTests
{
    [Fact]
    public void HardcodedDefault_reproduces_todays_receipt_behaviour()
    {
        var d = ReceiptSettingsValues.HardcodedDefault;

        d.Width.Should().Be(ReceiptWidth.Mm80);
        d.SalesHeaderText.Should().BeNull();
        d.SalesFooterText.Should().BeNull();
        d.SalesShowBranch.Should().BeTrue();
        d.SalesShowCashier.Should().BeTrue();
        d.SalesShowPaymentMethod.Should().BeTrue();
        d.SalesShowTaxLine.Should().BeTrue();
        d.SalesShowReferenceNumber.Should().BeTrue();
        d.DeliveryShowPrices.Should().BeTrue();
        d.DeliveryShowRelatedSaleNumber.Should().BeTrue();
        d.DeliveryShowContactNumber.Should().BeTrue();
        d.DeliveryShowSignatureFields.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run it, verify it fails** — `dotnet test --filter "FullyQualifiedName~ReceiptSettingsValuesTests"` → FAIL (types not defined).
- [ ] **Step 3: Create `ReceiptWidth.cs`**

```csharp
namespace Negosio.Domain.Enums;

/// <summary>Thermal print width for the Sales receipt. Persisted numerically; values must stay stable.</summary>
public enum ReceiptWidth
{
    Mm80 = 0,
    Mm58 = 1
}
```

- [ ] **Step 4: Create `ReceiptSettingsValues.cs`**

```csharp
using Negosio.Domain.Enums;

namespace Negosio.Application.Settings;

/// <summary>
/// A complete, self-contained snapshot of receipt presentation config — the shape shared by the
/// <c>ReceiptSettings</c> entity, the resolver, and the API DTO. There is no field-level merging:
/// a settings row is always one whole <see cref="ReceiptSettingsValues"/>.
/// </summary>
public sealed record ReceiptSettingsValues(
    ReceiptWidth Width,
    string? SalesHeaderText,
    string? SalesFooterText,
    bool SalesShowBranch,
    bool SalesShowCashier,
    bool SalesShowPaymentMethod,
    bool SalesShowTaxLine,
    bool SalesShowReferenceNumber,
    string? DeliveryHeaderText,
    string? DeliveryFooterText,
    bool DeliveryShowPrices,
    bool DeliveryShowRelatedSaleNumber,
    bool DeliveryShowContactNumber,
    bool DeliveryShowSignatureFields)
{
    /// <summary>Reproduces the receipt output that existed before Receipt Settings — used when a
    /// tenant has configured nothing. No custom header/footer (fall back to the business-info block
    /// and "Thank you!"), every option on, 80mm.</summary>
    public static ReceiptSettingsValues HardcodedDefault { get; } = new(
        Width: ReceiptWidth.Mm80,
        SalesHeaderText: null,
        SalesFooterText: null,
        SalesShowBranch: true,
        SalesShowCashier: true,
        SalesShowPaymentMethod: true,
        SalesShowTaxLine: true,
        SalesShowReferenceNumber: true,
        DeliveryHeaderText: null,
        DeliveryFooterText: null,
        DeliveryShowPrices: true,
        DeliveryShowRelatedSaleNumber: true,
        DeliveryShowContactNumber: true,
        DeliveryShowSignatureFields: true);
}
```

- [ ] **Step 5: Run test, verify PASS.**
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(receipts): ReceiptWidth enum + ReceiptSettingsValues snapshot record"`

---

## Task 2: `ReceiptText.Normalize` helper + validator

**Files:**
- Create: `src/Negosio.Application/Settings/ReceiptSettingsValidators.cs`
- Test: `tests/Negosio.UnitTests/Settings/ReceiptTextTests.cs`, `tests/Negosio.UnitTests/Settings/UpdateReceiptSettingsRequestValidatorTests.cs`

**Interfaces:**
- Consumes: `ReceiptSettingsValues` (Task 1).
- Produces:
  - `static class ReceiptText { static string? Normalize(string? raw); }` — trims; returns `null` for null/whitespace; strips control chars except `\n`; collapses runs of spaces/tabs to a single space; collapses 3+ newlines to 2; **throws `nothing`** (pure). Caller enforces length/line-count via the validator.
  - `sealed record UpdateReceiptSettingsRequest(ReceiptWidth Width, string? SalesHeaderText, string? SalesFooterText, bool SalesShowBranch, bool SalesShowCashier, bool SalesShowPaymentMethod, bool SalesShowTaxLine, bool SalesShowReferenceNumber, string? DeliveryHeaderText, string? DeliveryFooterText, bool DeliveryShowPrices, bool DeliveryShowRelatedSaleNumber, bool DeliveryShowContactNumber, bool DeliveryShowSignatureFields)` — put this record in `ReceiptSettingsContracts.cs` (Task 4) but the validator here references it; create a minimal stub of the record in this task and flesh the rest of the contracts file in Task 4. **Simpler: create `ReceiptSettingsContracts.cs` now with just this request record**, and add the DTO/interface in Task 4.
  - `sealed class UpdateReceiptSettingsRequestValidator : AbstractValidator<UpdateReceiptSettingsRequest>` — header/footer (both sales & delivery): after `ReceiptText.Normalize`, ≤ 500 chars and ≤ 6 lines; `Width` must be a defined enum value.

- [ ] **Step 1: Write failing tests**

```csharp
// ReceiptTextTests.cs
using FluentAssertions;
using Negosio.Application.Settings;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class ReceiptTextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  hi  ", "hi")]
    [InlineData("a\t\t  b", "a b")]
    [InlineData("line1\n\n\n\nline2", "line1\n\nline2")]
    public void Normalize_trims_collapses_and_nulls(string? input, string? expected)
        => ReceiptText.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_strips_control_chars_but_keeps_newlines()
        => ReceiptText.Normalize("a\u0007b\nc").Should().Be("ab\nc");
}
```

```csharp
// UpdateReceiptSettingsRequestValidatorTests.cs
using FluentAssertions;
using FluentValidation.TestHelper;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class UpdateReceiptSettingsRequestValidatorTests
{
    private static UpdateReceiptSettingsRequest Valid() => new(
        ReceiptWidth.Mm80, "Header", "Footer", true, true, true, true, true,
        "DHeader", "DFooter", true, true, true, true);

    private readonly UpdateReceiptSettingsRequestValidator _v = new();

    [Fact]
    public void Accepts_a_valid_request() => _v.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Rejects_header_over_500_chars()
        => _v.TestValidate(Valid() with { SalesHeaderText = new string('x', 501) })
             .ShouldHaveValidationErrorFor(x => x.SalesHeaderText);

    [Fact]
    public void Rejects_footer_with_too_many_lines()
        => _v.TestValidate(Valid() with { SalesFooterText = "a\nb\nc\nd\ne\nf\ng" })
             .ShouldHaveValidationErrorFor(x => x.SalesFooterText);

    [Fact]
    public void Rejects_undefined_width()
        => _v.TestValidate(Valid() with { Width = (ReceiptWidth)9 })
             .ShouldHaveValidationErrorFor(x => x.Width);
}
```

- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Create `ReceiptSettingsContracts.cs` with the request record only**

```csharp
using Negosio.Domain.Enums;

namespace Negosio.Application.Settings;

public sealed record UpdateReceiptSettingsRequest(
    ReceiptWidth Width,
    string? SalesHeaderText,
    string? SalesFooterText,
    bool SalesShowBranch,
    bool SalesShowCashier,
    bool SalesShowPaymentMethod,
    bool SalesShowTaxLine,
    bool SalesShowReferenceNumber,
    string? DeliveryHeaderText,
    string? DeliveryFooterText,
    bool DeliveryShowPrices,
    bool DeliveryShowRelatedSaleNumber,
    bool DeliveryShowContactNumber,
    bool DeliveryShowSignatureFields);
```

- [ ] **Step 4: Create `ReceiptSettingsValidators.cs`**

```csharp
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using Negosio.Domain.Enums;

namespace Negosio.Application.Settings;

public static class ReceiptText
{
    private static readonly Regex SpaceRuns = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex NewlineRuns = new(@"\n{3,}", RegexOptions.Compiled);

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '\n' || !char.IsControl(ch)) sb.Append(ch);
        }

        var collapsed = NewlineRuns.Replace(SpaceRuns.Replace(sb.ToString(), " "), "\n\n");
        // trim each line's trailing spaces, then trim the whole thing
        collapsed = string.Join('\n', collapsed.Split('\n').Select(l => l.TrimEnd()));
        collapsed = collapsed.Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    public static int LineCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + 1;
}

public sealed class UpdateReceiptSettingsRequestValidator : AbstractValidator<UpdateReceiptSettingsRequest>
{
    public UpdateReceiptSettingsRequestValidator()
    {
        RuleFor(x => x.Width).IsInEnum();

        foreach (var selector in new Func<UpdateReceiptSettingsRequest, string?>[]
                 { x => x.SalesHeaderText, x => x.SalesFooterText, x => x.DeliveryHeaderText, x => x.DeliveryFooterText })
        {
            // one rule per text field, evaluated against the normalized value
        }

        Text(x => x.SalesHeaderText, nameof(UpdateReceiptSettingsRequest.SalesHeaderText));
        Text(x => x.SalesFooterText, nameof(UpdateReceiptSettingsRequest.SalesFooterText));
        Text(x => x.DeliveryHeaderText, nameof(UpdateReceiptSettingsRequest.DeliveryHeaderText));
        Text(x => x.DeliveryFooterText, nameof(UpdateReceiptSettingsRequest.DeliveryFooterText));
    }

    private void Text(Expression<Func<UpdateReceiptSettingsRequest, string?>> selector, string name)
    {
        RuleFor(selector).Custom((value, ctx) =>
        {
            var normalized = ReceiptText.Normalize(value);
            if (normalized is null) return;
            if (normalized.Length > 500)
                ctx.AddFailure(name, "Keep this under 500 characters.");
            if (ReceiptText.LineCount(normalized) > 6)
                ctx.AddFailure(name, "Keep this to 6 lines or fewer.");
        });
    }
}
```

> Note: add `using System;` `using System.Linq;` `using System.Linq.Expressions;` as needed for compilation.

- [ ] **Step 5: Run tests, verify PASS.**
- [ ] **Step 6: Commit** — `git commit -am "feat(receipts): receipt text normalization + update-request validator"`

---

## Task 3: `ReceiptSettings` entity

**Files:**
- Create: `src/Negosio.Domain/Entities/Settings/ReceiptSettings.cs`
- Test: `tests/Negosio.UnitTests/Settings/ReceiptSettingsEntityTests.cs`

**Interfaces:**
- Consumes: `ReceiptSettingsValues` (Task 1).
- Produces:
  - `ReceiptSettings : Entity` with private-setter props mirroring `ReceiptSettingsValues` + `Guid TenantId`, `Guid? BranchId`, `Guid UpdatedByUserId`.
  - `static ReceiptSettings CreateDefault(Guid tenantId, Guid? branchId, Guid userId)` — initializes from `ReceiptSettingsValues.HardcodedDefault`.
  - `static ReceiptSettings CreateFrom(Guid tenantId, Guid? branchId, ReceiptSettingsValues seed, Guid userId)` — for the "seed a branch override from currently-effective settings" path.
  - `void Update(ReceiptSettingsValues values, Guid userId)` — overwrites every presentation field, sets `UpdatedByUserId`, calls `Touch()`. Header/footer stored **already normalized** (caller passes normalized values; see Task 5).
  - `ReceiptSettingsValues ToValues()` — projects the row back to the snapshot record.

- [ ] **Step 1: Write failing test**

```csharp
using FluentAssertions;
using Negosio.Application.Settings;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class ReceiptSettingsEntityTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void CreateDefault_matches_hardcoded_default()
    {
        var row = ReceiptSettings.CreateDefault(Tenant, null, User);
        row.TenantId.Should().Be(Tenant);
        row.BranchId.Should().BeNull();
        row.UpdatedByUserId.Should().Be(User);
        row.ToValues().Should().Be(ReceiptSettingsValues.HardcodedDefault);
    }

    [Fact]
    public void Update_overwrites_all_fields_and_restamps_user()
    {
        var row = ReceiptSettings.CreateDefault(Tenant, null, User);
        var editor = Guid.NewGuid();
        var v = ReceiptSettingsValues.HardcodedDefault with
        {
            Width = ReceiptWidth.Mm58, SalesHeaderText = "VERIFY CO", SalesShowCashier = false
        };

        row.Update(v, editor);

        row.ToValues().Should().Be(v);
        row.UpdatedByUserId.Should().Be(editor);
    }

    [Fact]
    public void CreateFrom_seeds_from_supplied_values()
    {
        var branch = Guid.NewGuid();
        var seed = ReceiptSettingsValues.HardcodedDefault with { SalesFooterText = "Come again" };
        var row = ReceiptSettings.CreateFrom(Tenant, branch, seed, User);
        row.BranchId.Should().Be(branch);
        row.ToValues().Should().Be(seed);
    }
}
```

- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement the entity** (follow `TenantProfile`/`SaleReturn` conventions — private ctor for EF, static factories, `Touch()`):

```csharp
using Negosio.Application.Settings; // ReceiptSettingsValues lives in Application — OR move the record to Domain.
```

> **Decision for the implementer:** `ReceiptSettingsValues` is referenced by both Domain (`ReceiptSettings`) and Application. Domain must not depend on Application. **Move `ReceiptSettingsValues.cs` to `src/Negosio.Domain/Common/ReceiptSettingsValues.cs`, namespace `Negosio.Domain.Common`.** Update Task 1/2 usings accordingly (`using Negosio.Domain.Common;`). Redo the Task 1 commit path if already committed — a follow-up move commit is fine.

Entity body:

```csharp
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

public class ReceiptSettings : Entity
{
    private ReceiptSettings() { }

    private ReceiptSettings(Guid tenantId, Guid? branchId, ReceiptSettingsValues v, Guid userId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        Apply(v);
        UpdatedByUserId = userId;
    }

    public Guid TenantId { get; private set; }
    public Guid? BranchId { get; private set; }

    public ReceiptWidth Width { get; private set; }
    public string? SalesHeaderText { get; private set; }
    public string? SalesFooterText { get; private set; }
    public bool SalesShowBranch { get; private set; }
    public bool SalesShowCashier { get; private set; }
    public bool SalesShowPaymentMethod { get; private set; }
    public bool SalesShowTaxLine { get; private set; }
    public bool SalesShowReferenceNumber { get; private set; }
    public string? DeliveryHeaderText { get; private set; }
    public string? DeliveryFooterText { get; private set; }
    public bool DeliveryShowPrices { get; private set; }
    public bool DeliveryShowRelatedSaleNumber { get; private set; }
    public bool DeliveryShowContactNumber { get; private set; }
    public bool DeliveryShowSignatureFields { get; private set; }

    public Guid UpdatedByUserId { get; private set; }

    public static ReceiptSettings CreateDefault(Guid tenantId, Guid? branchId, Guid userId)
        => new(tenantId, branchId, ReceiptSettingsValues.HardcodedDefault, userId);

    public static ReceiptSettings CreateFrom(Guid tenantId, Guid? branchId, ReceiptSettingsValues seed, Guid userId)
        => new(tenantId, branchId, seed, userId);

    public void Update(ReceiptSettingsValues values, Guid userId)
    {
        Apply(values);
        UpdatedByUserId = userId;
        Touch();
    }

    public ReceiptSettingsValues ToValues() => new(
        Width, SalesHeaderText, SalesFooterText, SalesShowBranch, SalesShowCashier,
        SalesShowPaymentMethod, SalesShowTaxLine, SalesShowReferenceNumber,
        DeliveryHeaderText, DeliveryFooterText, DeliveryShowPrices, DeliveryShowRelatedSaleNumber,
        DeliveryShowContactNumber, DeliveryShowSignatureFields);

    private void Apply(ReceiptSettingsValues v)
    {
        Width = v.Width;
        SalesHeaderText = v.SalesHeaderText;
        SalesFooterText = v.SalesFooterText;
        SalesShowBranch = v.SalesShowBranch;
        SalesShowCashier = v.SalesShowCashier;
        SalesShowPaymentMethod = v.SalesShowPaymentMethod;
        SalesShowTaxLine = v.SalesShowTaxLine;
        SalesShowReferenceNumber = v.SalesShowReferenceNumber;
        DeliveryHeaderText = v.DeliveryHeaderText;
        DeliveryFooterText = v.DeliveryFooterText;
        DeliveryShowPrices = v.DeliveryShowPrices;
        DeliveryShowRelatedSaleNumber = v.DeliveryShowRelatedSaleNumber;
        DeliveryShowContactNumber = v.DeliveryShowContactNumber;
        DeliveryShowSignatureFields = v.DeliveryShowSignatureFields;
    }
}
```

- [ ] **Step 4: Run tests, verify PASS.**
- [ ] **Step 5: Commit** — `git commit -am "feat(receipts): ReceiptSettings entity (whole-row snapshot)"`

---

## Task 4: Business-identity fields on `TenantProfile` + `Branch`

**Files:**
- Modify: `src/Negosio.Domain/Entities/Tenant/TenantProfile.cs`, `src/Negosio.Domain/Entities/Branch.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/TenantProfileConfiguration.cs`, `BranchConfiguration.cs`
- Modify: `src/Negosio.Application/Branches/BranchContracts.cs`, `BranchManagementService.cs`, `BranchQueryService.cs`, `BranchValidators.cs`
- Test: `tests/Negosio.UnitTests/...` (entity), `tests/Negosio.IntegrationTests/Branches/BranchManagementTests.cs` (contact number round-trips)

**Interfaces:**
- Produces:
  - `TenantProfile.ContactNumber : string?`, `TenantProfile.TaxId : string?`, `void TenantProfile.ConfigureBusinessInfo(string? contactNumber, string? taxId)` (trims, nulls empties, `Touch()`).
  - `Branch.ContactNumber : string?`; `Branch.Create(... , string? contactNumber)` and `Branch.UpdateDetails(... , string? contactNumber)` gain a trailing param.
  - `BranchDto` / `CreateBranchRequest` / `UpdateBranchRequest` gain `string? ContactNumber` (append — last positional field).

- [ ] **Step 1: Failing entity test** (`TenantProfileTests` — create if missing):

```csharp
[Fact]
public void ConfigureBusinessInfo_trims_and_nulls_empty()
{
    var p = TenantProfile.Create(Guid.NewGuid(), "Acme", BusinessType.Retail);
    p.ConfigureBusinessInfo("  0917 000 1234 ", "   ");
    p.ContactNumber.Should().Be("0917 000 1234");
    p.TaxId.Should().BeNull();
}
```

- [ ] **Step 2: Failing integration test** in `BranchManagementTests.cs`:

```csharp
[Fact]
public async Task Branch_contact_number_round_trips()
{
    await RegisterLoginAndAuthorizeAsync();
    var created = await Client.PostAsJsonAsync("/api/branches",
        new CreateBranchRequest("Contactful", "CF", "L1", null, "City", "Prov", null, "0917 555 0000"));
    var dto = (await created.Content.ReadFromJsonAsync<BranchDto>(TestJson.Options))!;
    dto.ContactNumber.Should().Be("0917 555 0000");
}
```

> The `CreateBranchRequest` positional list changes — update the existing `IntegrationTest.CreateBranchAsync` helper and every existing `new CreateBranchRequest(...)` / `new UpdateBranchRequest(...)` call site (grep: ~10 in tests) to pass the new trailing `null`/value. This is part of this task.

- [ ] **Step 3: Run, verify FAIL / compile errors listing the call sites.**
- [ ] **Step 4: Implement** — add properties + factory/param changes + EF `HasMaxLength(40)` columns + validator rules (`.MaximumLength(40)`, optional) + thread through `BranchManagementService.Create/Update` and both `Projection` expressions. Fix all call sites.
- [ ] **Step 5: Run tests (unit + `BranchManagementTests`), verify PASS.** Run the full `dotnet test` — the record-shape change touches many files; make sure nothing else broke.
- [ ] **Step 6: Commit** — `git commit -am "feat(settings): business contact number + tax id on TenantProfile/Branch"`

---

## Task 5: `ReceiptSettingsContracts` + `ReceiptSettingsService` (Get / Update / Reset)

**Files:**
- Modify: `src/Negosio.Application/Settings/ReceiptSettingsContracts.cs` (add DTO + interface)
- Create: `src/Negosio.Application/Settings/ReceiptSettingsService.cs`
- Modify: `src/Negosio.Application/Abstractions/ITenantDbContext.cs`, `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs` (DbSet)
- Modify: `src/Negosio.Application/DependencyInjection.cs`
- Test: covered by Task 7 integration tests (service has no independent HTTP surface until Task 6). Add focused service tests only if the resolver seam (Task 6b) is unavailable — otherwise defer to Task 7.

**Interfaces:**
- Consumes: `ReceiptSettingsValues`, `ReceiptSettings` entity, `ReceiptText.Normalize`, `IBranchAccessResolver` (`IsAllBranch`, `AssignedBranchIdAsync`, `ResolveTargetBranchAsync`), `ICurrentUser`.
- Produces:
  - `enum ReceiptSettingsScope { TenantDefault, Branch }`
  - `sealed record ReceiptSettingsDto(ReceiptSettingsScope Scope, Guid? BranchId, bool CanEdit, bool IsOverride, ReceiptWidth Width, string? SalesHeaderText, ... all fields ..., DateTime? UpdatedAtUtc, string? UpdatedByName)`
  - `interface IReceiptSettingsService { Task<ReceiptSettingsDto> GetAsync(Guid? branchId, CancellationToken); Task<ReceiptSettingsDto> UpdateAsync(Guid? branchId, UpdateReceiptSettingsRequest request, CancellationToken); Task ResetAsync(Guid branchId, CancellationToken); }`

**Behaviour rules (write these as the service):**
- `GetAsync(null)` → tenant-default row projected, or `HardcodedDefault` with `Scope=TenantDefault, IsOverride=false`. `CanEdit` = `IsAllBranch` (Owner/Admin).
- `GetAsync(branchId)` → branch row if it exists (`Scope=Branch, IsOverride=true`); else the effective tenant default (`Scope=Branch, IsOverride=false` — i.e. "showing inherited values for this branch"). `CanEdit` = `IsAllBranch || branchId == await AssignedBranchIdAsync()`.
- `UpdateAsync(null, ...)` → **Owner/Admin only**: if `!IsAllBranch` throw `ForbiddenAppException(ErrorCodes.BranchForbidden, "Managers can't edit the tenant default receipt settings.")`. Upsert the `BranchId == null` row.
- `UpdateAsync(branchId, ...)` → `await _branchAccess.ResolveTargetBranchAsync(branchId)` (throws `BRANCH_FORBIDDEN` for a Manager on another branch; validates the branch exists/active). If no branch row: `seed = (await GetAsync(branchId)).ToValues-equivalent` effective values, `ReceiptSettings.CreateFrom(tenant, branchId, seed, userId)`, then `.Update(normalizedRequestValues, userId)`. Else `.Update(...)`.
- `ResetAsync(branchId)` → same branch authorization; delete the branch row if present; no-op otherwise.
- All header/footer strings passed into the entity are `ReceiptText.Normalize`-d first.
- Validate the request via `IValidator<UpdateReceiptSettingsRequest>` (`ValidateAndThrowAppAsync`) before touching the DB.
- `UpdatedByName` = join `Users` on `UpdatedByUserId` → `FirstName + " " + LastName`.

- [ ] **Step 1:** Add `DbSet<ReceiptSettings> ReceiptSettings` to the interface and `TenantDbContext` (`=> Set<ReceiptSettings>()`).
- [ ] **Step 2:** Flesh `ReceiptSettingsContracts.cs` with `ReceiptSettingsScope`, `ReceiptSettingsDto`, `IReceiptSettingsService`.
- [ ] **Step 3:** Implement `ReceiptSettingsService` per the rules above. Register in `DependencyInjection.cs` under a new `// Receipt settings` block: `services.AddScoped<IReceiptSettingsService, ReceiptSettingsService>();`
- [ ] **Step 4:** `dotnet build` — verify it compiles. (Behavioural verification is Task 7.)
- [ ] **Step 5: Commit** — `git commit -am "feat(receipts): ReceiptSettingsService (get/update/reset, branch-scoped)"`

---

## Task 6: EF configuration + migration

**Files:**
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/ReceiptSettingsConfiguration.cs`
- Generate: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/*_AddReceiptSettingsAndBusinessInfo.cs`
- Test: `tests/Negosio.IntegrationTests/Settings/ReceiptSettingsSchemaTests.cs` (one smoke test: insert two rows, unique index behaviour)

**Interfaces:**
- Consumes: `ReceiptSettings`, `TenantProfile`/`Branch` new columns (Task 4).

- [ ] **Step 1: Write the config**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ReceiptSettingsConfiguration : IEntityTypeConfiguration<ReceiptSettings>
{
    public void Configure(EntityTypeBuilder<ReceiptSettings> b)
    {
        b.ToTable("ReceiptSettings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.BranchId);
        b.Property(x => x.Width).IsRequired().HasConversion<int>();

        b.Property(x => x.SalesHeaderText).HasMaxLength(500);
        b.Property(x => x.SalesFooterText).HasMaxLength(500);
        b.Property(x => x.DeliveryHeaderText).HasMaxLength(500);
        b.Property(x => x.DeliveryFooterText).HasMaxLength(500);

        b.Property(x => x.SalesShowBranch).IsRequired();
        b.Property(x => x.SalesShowCashier).IsRequired();
        b.Property(x => x.SalesShowPaymentMethod).IsRequired();
        b.Property(x => x.SalesShowTaxLine).IsRequired();
        b.Property(x => x.SalesShowReferenceNumber).IsRequired();
        b.Property(x => x.DeliveryShowPrices).IsRequired();
        b.Property(x => x.DeliveryShowRelatedSaleNumber).IsRequired();
        b.Property(x => x.DeliveryShowContactNumber).IsRequired();
        b.Property(x => x.DeliveryShowSignatureFields).IsRequired();

        b.Property(x => x.UpdatedByUserId).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        // exactly one tenant-default row
        b.HasIndex(x => x.TenantId)
            .IsUnique()
            .HasFilter("[BranchId] IS NULL")
            .HasDatabaseName("UX_ReceiptSettings_TenantDefault");
        // one row per branch
        b.HasIndex(x => new { x.TenantId, x.BranchId })
            .IsUnique()
            .HasFilter("[BranchId] IS NOT NULL")
            .HasDatabaseName("UX_ReceiptSettings_TenantId_BranchId");
    }
}
```

- [ ] **Step 2: Add the `TenantProfile`/`Branch` columns to their existing configs** — `builder.Property(p => p.ContactNumber).HasMaxLength(40);` etc. (Task 4 may have done this; verify.)
- [ ] **Step 3: Generate the migration**

```bash
dotnet tool restore
dotnet dotnet-ef migrations add AddReceiptSettingsAndBusinessInfo \
  --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
```

- [ ] **Step 4: Eyeball the generated `Up()`** — three `AddColumn` (TenantProfile.ContactNumber, TenantProfile.TaxId, Branch.ContactNumber, all `nullable: true`), one `CreateTable("ReceiptSettings")`, two filtered unique indexes. No `DropColumn`, no data statements.
- [ ] **Step 5: Write the schema smoke test**

```csharp
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Settings;

public class ReceiptSettingsSchemaTests : IntegrationTest
{
    public ReceiptSettingsSchemaTests(NegosioApiFactory factory) : base(factory) { }

    [Fact]
    public async Task Tenant_default_and_branch_rows_coexist_but_duplicates_are_rejected()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var tenantId = await GetTenantIdAsync(owner);          // add helper if missing, or read from token
        var branchId = await GetMainBranchIdAsync(owner);

        await InScopeAsync(async db =>
        {
            db.ReceiptSettings.Add(ReceiptSettings.CreateDefault(tenantId, null, owner.UserId));
            db.ReceiptSettings.Add(ReceiptSettings.CreateDefault(tenantId, branchId, owner.UserId));
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsAnyAsync<Exception>(() => InScopeAsync(async db =>
        {
            db.ReceiptSettings.Add(ReceiptSettings.CreateDefault(tenantId, null, owner.UserId));
            await db.SaveChangesAsync();
        }));
    }
}
```

> If `InScopeAsync` / `GetTenantIdAsync` signatures differ, adapt to the real `IntegrationTest` helpers (see `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs`). The factory applies migrations automatically on first use.

- [ ] **Step 6: Run** `dotnet test --filter "FullyQualifiedName~ReceiptSettingsSchemaTests"` → PASS.
- [ ] **Step 7: Commit** — `git commit -am "feat(receipts): ReceiptSettings EF config + tenant migration"`

---

## Task 7: `ReceiptSettingsController` + `ReceiptSettingsManage` policy + `IReceiptSettingsResolver`

**Files:**
- Create: `src/Negosio.Api/Controllers/ReceiptSettingsController.cs`
- Create: `src/Negosio.Application/Settings/IReceiptSettingsResolver.cs` + `ReceiptSettingsResolver.cs`
- Modify: `src/Negosio.Api/Authorization/AuthorizationPolicies.cs`, `src/Negosio.Application/DependencyInjection.cs`
- Test: `tests/Negosio.IntegrationTests/Settings/ReceiptSettingsTests.cs`

**Interfaces:**
- Produces:
  - `AuthorizationPolicies.ReceiptSettingsManage = "ReceiptSettingsManage"` → `RoleNames(ManagementRoles)` (Owner/Admin/Manager).
  - `interface IReceiptSettingsResolver { Task<ReceiptSettingsValues> ResolveAsync(Guid branchId, CancellationToken ct = default); }` — memoized per request (`Dictionary<Guid, ReceiptSettingsValues>` field). Query: branch row where `TenantId == t && BranchId == branchId`; else row where `BranchId == null`; else `ReceiptSettingsValues.HardcodedDefault`.
  - `ReceiptSettingsController`: `GET /api/settings/receipts?branchId=` (policy `SalesView`), `PUT` (policy `ReceiptSettingsManage`), `DELETE` (policy `ReceiptSettingsManage`, `branchId` required).

- [ ] **Step 1: Write the integration tests (failing)**

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Settings;

public class ReceiptSettingsTests : IntegrationTest
{
    public ReceiptSettingsTests(NegosioApiFactory factory) : base(factory) { }

    private static UpdateReceiptSettingsRequest Req(string? header = "HDR", string? footer = "FTR",
        ReceiptWidth width = ReceiptWidth.Mm80) => new(
        width, header, footer, true, true, true, true, true,
        "DHDR", "DFTR", true, true, true, true);

    [Fact]                                                            // acceptance 1, 4, 5, 6
    public async Task Owner_edits_tenant_default_and_it_persists()
    {
        await RegisterLoginAndAuthorizeAsync();
        var put = await Client.PutAsJsonAsync("/api/settings/receipts", Req(header: "VERIFY CO", footer: "Salamat"));
        put.EnsureSuccessStatusCode();

        var got = await Client.GetFromJsonAsync<ReceiptSettingsDto>("/api/settings/receipts", TestJson.Options);
        got!.SalesHeaderText.Should().Be("VERIFY CO");
        got.SalesFooterText.Should().Be("Salamat");
        got.Scope.Should().Be(ReceiptSettingsScope.TenantDefault);
        got.UpdatedByName.Should().NotBeNullOrEmpty();
    }

    [Fact]                                                            // acceptance 2
    public async Task Manager_can_edit_only_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var other = await CreateBranchAsync("BGC", "BGC");
        var mgrToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainId);
        Authorize(mgrToken);

        (await Client.PutAsJsonAsync("/api/settings/receipts", Req()))                     // tenant default
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.PutAsJsonAsync($"/api/settings/receipts?branchId={other.Id}", Req())) // another branch
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.PutAsJsonAsync($"/api/settings/receipts?branchId={mainId}", Req()))    // own branch
            .EnsureSuccessStatusCode();
    }

    [Fact]                                                            // acceptance 3
    public async Task Cashier_cannot_edit_receipt_settings()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        Authorize(await AddTenantUserTokenAsync("cash@example.com", UserRole.Cashier, branchId));
        (await Client.PutAsJsonAsync("/api/settings/receipts", Req())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Branch_override_is_seeded_from_effective_settings_then_reset_falls_back()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        await Client.PutAsJsonAsync("/api/settings/receipts", Req(header: "TENANT HDR", footer: "tenant ftr"));

        // first branch write: change only the footer, header should have been seeded from tenant default
        var branchReq = Req(header: "TENANT HDR", footer: "branch ftr");
        (await Client.PutAsJsonAsync($"/api/settings/receipts?branchId={branchId}", branchReq)).EnsureSuccessStatusCode();

        var branchGot = await Client.GetFromJsonAsync<ReceiptSettingsDto>(
            $"/api/settings/receipts?branchId={branchId}", TestJson.Options);
        branchGot!.SalesFooterText.Should().Be("branch ftr");
        branchGot.IsOverride.Should().BeTrue();

        (await Client.DeleteAsync($"/api/settings/receipts?branchId={branchId}")).EnsureSuccessStatusCode();
        var afterReset = await Client.GetFromJsonAsync<ReceiptSettingsDto>(
            $"/api/settings/receipts?branchId={branchId}", TestJson.Options);
        afterReset!.SalesFooterText.Should().Be("tenant ftr");
        afterReset.IsOverride.Should().BeFalse();
    }

    [Fact]                                                            // acceptance 9 / VALIDATION
    public async Task Oversized_header_is_rejected()
    {
        await RegisterLoginAndAuthorizeAsync();
        var res = await Client.PutAsJsonAsync("/api/settings/receipts", Req(header: new string('x', 501)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Run, verify FAIL** (404 / not registered).
- [ ] **Step 3: Add the policy** to `AuthorizationPolicies.cs` — const + `options.AddPolicy(ReceiptSettingsManage, policy => policy.RequireAuthenticatedUser().RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));` under a `// ---- Receipt settings ----` comment.
- [ ] **Step 4: Implement the controller**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Settings;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings/receipts")]
public sealed class ReceiptSettingsController : ControllerBase
{
    private readonly IReceiptSettingsService _service;

    public ReceiptSettingsController(IReceiptSettingsService service) => _service = service;

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.SalesView)]
    [ProducesResponseType(typeof(ReceiptSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReceiptSettingsDto>> Get([FromQuery] Guid? branchId, CancellationToken ct)
        => Ok(await _service.GetAsync(branchId, ct));

    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.ReceiptSettingsManage)]
    [ProducesResponseType(typeof(ReceiptSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReceiptSettingsDto>> Put(
        [FromQuery] Guid? branchId, [FromBody] UpdateReceiptSettingsRequest request, CancellationToken ct)
        => Ok(await _service.UpdateAsync(branchId, request, ct));

    [HttpDelete]
    [Authorize(Policy = AuthorizationPolicies.ReceiptSettingsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete([FromQuery] Guid branchId, CancellationToken ct)
    {
        await _service.ResetAsync(branchId, ct);
        return NoContent();
    }
}
```

- [ ] **Step 5: Implement `IReceiptSettingsResolver` + registration.** Register both resolver and (from Task 5) service in `DependencyInjection.cs`.
- [ ] **Step 6: Run `ReceiptSettingsTests`, iterate to green.** Then full `dotnet test`.
- [ ] **Step 7: Commit** — `git commit -am "feat(receipts): receipt-settings endpoints + policy + resolver"`

---

## Task 8: Extend `ReceiptDto` + wire `ReceiptService` to settings & identity

**Files:**
- Modify: `src/Negosio.Application/Sales/SaleContracts.cs`, `src/Negosio.Application/Sales/ReceiptService.cs`
- Test: `tests/Negosio.IntegrationTests/Sales/ReceiptTests.cs` (create; or extend `CheckoutTests` receipt assertions)

**Interfaces:**
- Consumes: `IReceiptSettingsResolver.ResolveAsync(branchId)`, `TenantProfile.ContactNumber/TaxId`, `Branch.AddressLine1/City/Province/ContactNumber`.
- Produces (final `ReceiptDto` shape — append only, keep existing order):

```csharp
public sealed record ReceiptPaymentDto(
    string Method, decimal Amount,
    string? ReferenceNumber, decimal? ReceivedAmount, decimal? ChangeAmount);   // D8 — appended

public sealed record ReceiptDto(
    string StoreName, string BranchName, string RegisterName, string SaleNumber, string CashierName,
    DateTime CreatedAtUtc, IReadOnlyList<ReceiptLineDto> Lines,
    decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal GrandTotal,
    IReadOnlyList<ReceiptPaymentDto> Payments, decimal ChangeDue, SaleStatus Status,
    // --- appended for Receipt Settings ---
    string? HeaderText, string? FooterText,
    string? BusinessAddress, string? BusinessContactNumber, string? TaxId,
    bool ShowBranch, bool ShowCashier, bool ShowPaymentMethod, bool ShowTaxLine, bool ShowReferenceNumber,
    ReceiptWidth Width);
```

- [ ] **Step 1: Failing test** — default settings ⇒ new fields carry the fallback and existing fields are unchanged:

```csharp
[Fact]                                                                     // acceptance 8, 10, 11, 12
public async Task Receipt_with_no_settings_configured_uses_safe_defaults()
{
    var scene = await ArrangeSaleAsync();   // reuse Checkout arrange helpers; make one Cash sale
    var receipt = await Client.GetFromJsonAsync<ReceiptDto>($"/api/sales/{scene.SaleId}/receipt", TestJson.Options);

    receipt!.HeaderText.Should().BeNull();          // → frontend shows business-info block
    receipt.FooterText.Should().BeNull();           // → frontend shows "Thank you!"
    receipt.ShowBranch.Should().BeTrue();
    receipt.ShowReferenceNumber.Should().BeTrue();
    receipt.Width.Should().Be(ReceiptWidth.Mm80);
    receipt.GrandTotal.Should().Be(scene.ExpectedGrandTotal);   // unchanged authoritative value
    receipt.Payments[0].ReceivedAmount.Should().Be(scene.CashReceived);
}

[Fact]                                                                     // acceptance 7, 13
public async Task Receipt_reflects_saved_header_and_footer()
{
    var scene = await ArrangeSaleAsync();
    await Client.PutAsJsonAsync("/api/settings/receipts",
        new UpdateReceiptSettingsRequest(ReceiptWidth.Mm58, "MY STORE", "No refunds",
            true, false, true, true, false, null, null, true, true, true, true));

    var receipt = await Client.GetFromJsonAsync<ReceiptDto>($"/api/sales/{scene.SaleId}/receipt", TestJson.Options);
    receipt!.HeaderText.Should().Be("MY STORE");
    receipt.FooterText.Should().Be("No refunds");
    receipt.ShowCashier.Should().BeFalse();
    receipt.ShowReferenceNumber.Should().BeFalse();
    receipt.Width.Should().Be(ReceiptWidth.Mm58);
}
```

- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Extend the records** in `SaleContracts.cs`.
- [ ] **Step 4: Update `ReceiptService.GetReceiptAsync`** — after loading the sale, `var settings = await _resolver.ResolveAsync(sale.BranchId, ct);` (inject `IReceiptSettingsResolver`); load `TenantProfile.ContactNumber/TaxId`; compose `BusinessAddress` from the branch (`$"{AddressLine1}, {City}, {Province}"`, skip blanks); map payments with the 3 new fields (already on `Payment`); pass `settings.SalesHeaderText` etc. and `settings.Width`.
- [ ] **Step 5: Run tests; then full `dotnet test`** — the pre-existing `CheckoutTests` receipt assertions must still pass (existing fields unchanged).
- [ ] **Step 6: Commit** — `git commit -am "feat(receipts): ReceiptDto carries configured header/footer/business-info/width"`

---

## Task 9: Frontend API + types + query hook

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`
- Create: `web/negosio-web/src/api/receiptSettings.ts`, `web/negosio-web/src/hooks/useReceiptSettings.ts`
- Modify: `web/negosio-web/src/lib/useCan.ts` (add `'receipt:settings'`)

**Interfaces:**
- Produces:
  - `types.ts`: `type ReceiptWidth = 'Mm80' | 'Mm58'`; `interface ReceiptSettingsDto { scope: 'TenantDefault' | 'Branch'; branchId: string | null; canEdit: boolean; isOverride: boolean; width: ReceiptWidth; salesHeaderText: string | null; salesFooterText: string | null; salesShowBranch: boolean; salesShowCashier: boolean; salesShowPaymentMethod: boolean; salesShowTaxLine: boolean; salesShowReferenceNumber: boolean; deliveryHeaderText: string | null; deliveryFooterText: string | null; deliveryShowPrices: boolean; deliveryShowRelatedSaleNumber: boolean; deliveryShowContactNumber: boolean; deliveryShowSignatureFields: boolean; updatedAtUtc: string | null; updatedByName: string | null }`; `type UpdateReceiptSettingsRequest = Omit<ReceiptSettingsDto, 'scope'|'branchId'|'canEdit'|'isOverride'|'updatedAtUtc'|'updatedByName'>`; extend the existing receipt DTO type + its payment type with the new fields; add `contactNumber` to branch types.
  - `receiptSettingsApi.get(branchId?: string)`, `.update(branchId: string | undefined, body: UpdateReceiptSettingsRequest)`, `.reset(branchId: string)` — query-string `?branchId=` only when defined.
  - `RECEIPT_SETTINGS_QUERY_KEY = (branchId?: string) => ['settings', 'receipts', branchId ?? 'tenant'] as const`; `useReceiptSettings(branchId?)`.
  - `useCan`: `'receipt:settings': new Set<UserRole>(['Owner', 'Admin', 'Manager'])` with a `// Mirrors AuthorizationPolicies.ReceiptSettingsManage` comment.

- [ ] **Step 1:** Add types. `npx tsc --noEmit` — expect errors only where `ReceiptPage` now lacks new fields (fixed in Task 11) — or make the new `ReceiptDto` fields optional in the type to stage the change; prefer required + fix in same PR.
- [ ] **Step 2:** Write `receiptSettings.ts` + `useReceiptSettings.ts` mirroring `settings.ts` / `useTaxSettings.ts` conventions (`apiRequest`, `staleTime: 5 * 60_000`).
- [ ] **Step 3:** Update `useCan.ts`.
- [ ] **Step 4: Commit** — `git commit -am "feat(web): receipt-settings api client, types, capability"`

---

## Task 10: Shared receipt primitives

**Files:**
- Create: `web/negosio-web/src/components/receipt/ReceiptBusinessInfo.tsx`, `ReceiptHeader.tsx`, `ReceiptFooter.tsx`, `receiptStyles.ts`
- Create: `web/negosio-web/src/components/ui/TextArea.tsx` + export from `components/ui/index.ts`

**Interfaces:**
- Produces:
  - `ReceiptBusinessInfo({ businessName, branchName, address, contactNumber, taxId, showBranch }: {...})` — renders `<h1>` name, optional branch line, optional address / contact / TIN lines. Plain text.
  - `ReceiptHeader({ headerText, business }: { headerText: string | null; business: React.ComponentProps<typeof ReceiptBusinessInfo> })` — if `headerText` → render each line in a centered block; else `<ReceiptBusinessInfo {...business} />`.
  - `ReceiptFooter({ footerText }: { footerText: string | null })` — `footerText` split on `\n` into `<p class="center muted">` lines; when `null` render the single line `Thank you!` (preserves today's output).
  - `thermalReceiptCss(width: ReceiptWidth): string` — today's `RECEIPT_CSS` with `.receipt { width: 80mm }` / `72mm` font tweak for `Mm58` (`width:58mm; font-size:11px`).
  - `TextArea` — `label/error/hint` like `TextField`, `rows` prop, same `inputClass` base minus the fixed height.

- [ ] **Step 1:** Build `receiptStyles.ts` by lifting the existing `RECEIPT_CSS` string from `ReceiptPage.tsx` into a function; `Mm58` branch narrows width + font.
- [ ] **Step 2:** Build the three components. No `dangerouslySetInnerHTML` anywhere. Newlines → separate elements.
- [ ] **Step 3:** Build `TextArea`, export it.
- [ ] **Step 4:** `npx tsc --noEmit` clean for the new files.
- [ ] **Step 5: Commit** — `git commit -am "feat(web): shared ReceiptHeader/Footer/BusinessInfo + TextArea"`

---

## Task 11: Refactor `ReceiptPage.tsx` onto the shared primitives + new fields

**Files:**
- Modify: `web/negosio-web/src/pages/ReceiptPage.tsx`
- Test: manual/live (Task 14). Optionally a small RTL test if the repo has React Testing Library set up — check `web/negosio-web/package.json`; if not, skip (don't add the toolchain here).

- [ ] **Step 1:** Replace the inline `RECEIPT_CSS` with `thermalReceiptCss(r.width)`.
- [ ] **Step 2:** Replace the hardcoded `<h1>{r.storeName}</h1><p>{r.branchName}</p>` with `<ReceiptHeader headerText={r.headerText} business={{ businessName: r.storeName, branchName: r.branchName, address: r.businessAddress, contactNumber: r.businessContactNumber, taxId: r.taxId, showBranch: r.showBranch }} />`.
- [ ] **Step 3:** Gate the Register/Cashier rows on `r.showCashier` (cashier) — keep Register always (not in scope). Gate the Tax row on `r.showTaxLine`. Gate each payment's reference line (new: show `p.referenceNumber` when `r.showReferenceNumber && p.referenceNumber`). Show `Tendered` / `Change` per-payment when `p.receivedAmount != null`.
- [ ] **Step 4:** Replace the trailing `<p class="center muted">Thank you!</p>` with `<ReceiptFooter footerText={r.footerText} />`.
- [ ] **Step 5:** `npx tsc --noEmit` + `npm run build` clean.
- [ ] **Step 6: Commit** — `git commit -am "feat(web): render Sales receipt from configured settings"`

---

## Task 12: Settings tab shell + routes + `/settings/receipts` scaffold

**Files:**
- Create: `web/negosio-web/src/components/settings/SettingsTabs.tsx`
- Modify: `web/negosio-web/src/pages/SettingsPage.tsx` (split Tax card into `/settings/tax`)
- Create: `web/negosio-web/src/pages/settings/ReceiptSettingsPage.tsx` (scaffold — full form is Task 13)
- Modify: `web/negosio-web/src/App.tsx`, `web/negosio-web/src/lib/nav.ts`

**Design notes:**
- `nav.ts`: keep the single **Settings** row (routes to `/settings`). Do **not** add a second "Receipt settings" nav row — the spec's "Settings → Receipt Settings" is satisfied by the in-page `Tax | Receipts` tab. (If the user later wants it as its own nav child, that's a one-line follow-up.) Rationale recorded in the spec's §7.4.
- `/settings` redirects to `/settings/tax`.
- `/settings/receipts` is wrapped in `<RequireCapability capability="receipt:settings" title="Receipt settings">`.
- `SettingsTabs` renders two `NavLink`s (`/settings/tax`, `/settings/receipts`), styled like the app's existing horizontal tabs (check `ProductDetailPage` / `Reports` for the pattern; if none, a simple `border-b` active-underline).

- [ ] **Step 1:** `SettingsTabs.tsx` — the two-link bar. `/settings/receipts` link hidden unless `useCan('receipt:settings')`.
- [ ] **Step 2:** `SettingsPage.tsx` → `TaxSettingsPage` content moved under a `DashboardLayout title="Settings"` + `<SettingsTabs/>` + the existing `<TaxSettingsCard/>`. Keep `TaxSettingsCard` as-is.
- [ ] **Step 3:** `ReceiptSettingsPage.tsx` scaffold — `DashboardLayout` + `SettingsTabs` + `PageHeader title="Receipt settings" description="Customize the information and appearance shown on printed receipts."` + a `"Coming in the next task"` placeholder body.
- [ ] **Step 4:** `App.tsx` routes: replace `{ path: '/settings', element: <SettingsPage /> }` with `/settings` → `<Navigate to="/settings/tax" replace />`, `/settings/tax` → `<SettingsPage/>`, `/settings/receipts` → guarded `<ReceiptSettingsPage/>`.
- [ ] **Step 5:** `npx tsc --noEmit` + `npm run build` clean. Click through `/settings`, `/settings/tax`, `/settings/receipts` in dev.
- [ ] **Step 6: Commit** — `git commit -am "feat(web): Settings tab shell + /settings/receipts route"`

---

## Task 13: Receipt Settings form (General / Sales / Delivery) + live preview

**Files:**
- Modify: `web/negosio-web/src/pages/settings/ReceiptSettingsPage.tsx`
- Create: `web/negosio-web/src/components/settings/ReceiptPreview.tsx`
- Modify: `web/negosio-web/src/api/settings.ts` (+ `businessInfo` get/put) and `types.ts` (`BusinessInfoDto`)

**Behaviour:**
- **Branch selector** at the top (General tab): Owner/Admin see `Tenant default` + every branch (`branchesApi.list({ includeInactive: true })`); Manager sees only their branch (the list endpoint already returns just theirs). Selecting a scope drives `useReceiptSettings(branchId)`.
- Form seeded from the query data on load / scope change (same `useEffect` reseed pattern as `TaxSettingsCard`).
- **General tab:** business name (read-only, from `authMe`/profile with a link to where it's edited), Contact number + Tax/TIN (`/api/settings/business-info`, Owner/Admin only — `useCan('settings:write')`), Receipt width radio (`80mm` / `58mm compact`).
- **Sales receipt tab:** `TextArea` header, checkboxes (Show branch / cashier / payment method / tax / reference number), `TextArea` footer. Right pane: `<ReceiptPreview kind="sales" values={formValues} business={businessValues} />`.
- **Delivery receipt tab:** `TextArea` header, checkboxes (Show prices / related Sale # / contact number / signature fields), `TextArea` footer, preview `kind="delivery"`. (The Delivery *document* itself is Plan B; this tab just persists the config now.)
- **Save changes** → `receiptSettingsApi.update(selectedBranchId, body)` (body = all 14 fields from the merged form). `onSuccess` invalidate `RECEIPT_SETTINGS_QUERY_KEY(branchId)` + toast. Dirty tracking + Discard, mirroring `TaxSettingsCard`.
- **Reset to tenant defaults** — visible only when a branch scope is selected AND `isOverride`; `ConfirmDialog` → `receiptSettingsApi.reset(branchId)` → invalidate.
- "Last updated by {updatedByName} · {date}" line when present.
- 403 handling: toast "You don't have permission to change receipt settings." (mirror `TaxSettingsCard`).

- [ ] **Step 1:** `ReceiptPreview.tsx` — wraps `ReceiptHeader`/`ReceiptFooter` (sales) or a lightweight delivery mock, inside a scaled/bordered box, labelled **"Preview — not saved"**. Pure function of props; no fetch.
- [ ] **Step 2:** Build the General tab (+ `business-info` api/types).
- [ ] **Step 3:** Build the Sales tab + wire preview.
- [ ] **Step 4:** Build the Delivery tab + wire preview.
- [ ] **Step 5:** Save / Discard / Reset wiring.
- [ ] **Step 6:** `npx tsc --noEmit` + `npm run build` clean.
- [ ] **Step 7: Commit** — `git commit -am "feat(web): Receipt Settings form with live preview"`

---

## Task 14: Live verification + docs

**Files:**
- Modify: `docs/handover.md`, `C:\Users\Ace\Desktop\negosio-status.md`, `README.md` (Settings section if present)

- [ ] **Step 1:** Confirm servers: API `:5170` (`dotnet run` from `src/Negosio.Api`), Vite `:5173` (`npm run dev` from `web/negosio-web`). Seeded dev accounts: `owner.*@negosio.dev` / `manager.*@negosio.dev` (branch BGC) / `cashier2.*@negosio.dev`, password `Passw0rd!23`.
- [ ] **Step 2:** Headless-Chrome verification pass (per the `visual-verify-headless-chrome` memory recipe). Check:
  - Owner: `/settings/receipts` → set header "VERIFY COMPANY", footer "Salamat po", uncheck "Show cashier", save. Open any recent sale → **Print receipt**: header/footer applied, cashier row gone, layout intact. Reload the sale → still applied (acceptance 13).
  - Owner: switch scope to BGC branch, change footer only → save → confirm header inherited from tenant default (acceptance: branch seeded from effective). "Reset to tenant defaults" → footer reverts.
  - Manager (BGC): `/settings/receipts` shows only BGC; no "Tenant default" option; save works; `PUT ?branchId=<main>` via devtools → 403.
  - Cashier: no Receipts tab; `/settings/receipts` → access-denied page, never data.
  - A fresh tenant (register a new one) that never opens settings → print a receipt → **byte-identical to old output** (header = business+branch, footer = "Thank you!"). Acceptance 8.
  - 58mm width → receipt visibly narrower; still readable.
  - Zero console errors on every pass.
- [ ] **Step 3:** `dotnet test` full suite green (was 320; expect +~12). `npx tsc --noEmit` + `npm run build` clean.
- [ ] **Step 4:** Update `docs/handover.md` (new "Receipt Settings (Plan A)" section: what shipped, the `HARDCODED_DEFAULT` fallback contract, the whole-row resolution rule, `ReceiptSettingsManage` policy, that Plan B / Delivery Receipt is next) and `negosio-status.md` (add phase line). Note the branch is unmerged pending review.
- [ ] **Step 5: Commit** — `git commit -am "docs: Receipt Settings (Plan A) — handover + status"`

---

## Self-Review (completed by plan author)

**Spec coverage:**
- §3 D1 whole-row resolution → Tasks 1, 3, 7 (resolver), 7 tests (seed + reset). ✓
- §3 D2 policy → Task 7. ✓
- §3 D3 identity fields → Task 4. ✓
- §3 D7 width → Tasks 1, 8, 10, 11, 13. ✓
- §3 D8 payment fields → Task 8, 11. ✓
- §4.1 entity + filtered unique indexes → Tasks 3, 6. ✓
- §5 fallback / no seed row → Task 1 `HardcodedDefault`, Task 7 resolver, Task 8 test, Task 14 fresh-tenant check. ✓
- §6.1 endpoints incl. DELETE reset → Task 7. ✓
- §6.3 `ReceiptDto` extension → Task 8. ✓
- §7.1 shared primitives → Task 10. ✓
- §7.2 ReceiptPage refactor → Task 11. ✓
- §7.4 settings UI + tabs + preview + reset → Tasks 12, 13. ✓
- §8 preview vs authoritative → Task 13 Step 1 (pure component, shared render code). ✓
- §9 validation limits → Task 2. ✓
- §10 audit → Tasks 3 (`UpdatedByUserId`), 5 (`UpdatedByName`), 7 test. ✓
- §12 acceptance 1–13 → mapped inline in Tasks 7, 8, 14. (14–20 are Plan B.) ✓
- `DocumentNumberType.DeliveryReceipt = 5` staged → Task list "Backend — modified" + fold into Task 8 commit or Task 3; **implementer: add it in Task 3's commit.** ✓

**Placeholder scan:** validator `Text(...)` helper needs `using System; System.Linq; System.Linq.Expressions;` — noted inline. `GetTenantIdAsync` helper may not exist — noted "add if missing". No TODO/TBD left.

**Type consistency:** `ReceiptSettingsValues` field names identical across entity (`Apply`/`ToValues`), DTO, request record, TS interface. `ReceiptWidth` = `Mm80|Mm58` in C# and TS. `IReceiptSettingsResolver.ResolveAsync(Guid branchId)` returns `ReceiptSettingsValues` — consumed only by `ReceiptService` (Task 8) and Plan B.

**Open nit for the executor:** `ReceiptSettingsValues` location — plan says move to `Negosio.Domain.Common` in Task 3. Do that move *before* the Task 1 commit if executing top-to-bottom (adjust Task 1 `using`), or as a small dedicated commit. Either is fine.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-09-receipt-settings.md`.** Plan B (Delivery Receipt) is a separate file and depends on this one being merged-or-at-least-working (`IReceiptSettingsResolver`, the shared receipt primitives, `DocumentNumberType.DeliveryReceipt`).
