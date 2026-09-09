# Delivery Receipt Implementation Plan (Plan B of 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent **Delivery Receipt** document — its own `DR-000001` number sequence, generated from an existing Sale plus manually-entered recipient/address/notes, line items snapshotted — printable on A4/Letter, reprintable as the exact same document, created from the Sale-detail page.

**Architecture:** New `DeliveryReceipt` + `DeliveryReceiptItem` aggregate in the tenant DB. `DeliveryReceiptService` allocates the number via the existing `IDocumentNumberService` (new `DocumentNumberType.DeliveryReceipt`, branch-scoped counter), snapshots items from the sale, and enforces **one DR per Sale at the service layer only** (no DB unique constraint — multiple/partial deliveries stay possible later). The React `DeliveryReceiptPage` renders an A4 layout that reuses `ReceiptHeader`/`ReceiptFooter`/`ReceiptBusinessInfo` from Plan A and reads the delivery header/footer/toggles from the Plan A `ReceiptSettings`.

**Tech Stack:** Same as Plan A — .NET 9 / EF Core 9 / FluentValidation / xUnit; React 19 / TS strict / Vite / React Router 7 / TanStack Query v5 / Tailwind v4.

**Spec:** `docs/superpowers/specs/2026-09-09-receipt-settings-design.md` (APPROVED 2026-09-09) — §4.2, §4.3, §6.2, §7.3, §12 (acceptance 14–20).

**Depends on:** Plan A (`docs/superpowers/plans/2026-09-09-receipt-settings.md`) — specifically `IReceiptSettingsResolver.ResolveAsync`, `ReceiptSettingsValues` (delivery fields), the `web/.../components/receipt/` primitives, and `DocumentNumberType.DeliveryReceipt = 5` (added in Plan A Task 3). Do not start Plan B until Plan A's tasks 1–11 are merged or at least green on `feature/receipt-settings`.

**Branch:** continue on `feature/receipt-settings` (or a child `feature/delivery-receipt` branched from it — implementer's choice; the user reviews before any merge).

## Global Constraints

- **Currency PHP**, timezone **Asia/Manila (UTC+8)** fixed — render the DR date with the same client helpers the app already uses.
- **Snapshots only.** `DeliveryReceiptItem` stores `ProductNameSnapshot` / `VariantNameSnapshot` / `Quantity` / `UnitPrice?` copied at creation. A later catalog rename/reprice must not change an issued DR. Same rule as `SaleItem`.
- **Persisted, reprint-stable.** Reprinting a DR fetches the same row and shows the same `Number`. Never allocate a new number on reprint.
- **One DR per Sale — service-enforced, not DB-enforced.** `POST` for a sale that already has a DR returns the existing one (HTTP 200) with no new number. **No unique index on `SaleId`.** A non-unique `(TenantId, SaleId)` index for lookup only. This deliberately leaves multiple/partial deliveries per sale open for a future phase.
- **`DR-000001` format** via `IDocumentNumberService.NextAsync(tenantId, branchId, DocumentNumberType.DeliveryReceipt, branchCode, ct)` inside the create transaction. Branch-scoped counter (real `branchId`), consistent with Sale/Return.
- **No Customer entity, no delivery tracking/status, no digital signature, no POS button.** Recipient/address/contact/notes are free text on the DR. "Delivered by / Received by / signature / date received" are **blank ruled lines on the printout only.**
- **Branch scoping** identical to `ReceiptService`: a branch-scoped user acting on another branch's sale/DR gets 404.
- **Delivery header/footer/toggles come from Plan A `ReceiptSettings`** (`DeliveryHeaderText`, `DeliveryFooterText`, `DeliveryShowPrices`, `DeliveryShowRelatedSaleNumber`, `DeliveryShowContactNumber`, `DeliveryShowSignatureFields`), resolved for the DR's branch. `DeliveryShowPrices = false` ⇒ the DTO omits/nulls unit price + amount and the page hides those columns.
- **EF migration** (tenant context): `dotnet dotnet-ef migrations add AddDeliveryReceipts --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb`. Additive tables only.
- **Do not merge, do not push.** Frequent commits. `dotnet test` + `npx tsc --noEmit` + `npm run build` stay green.

---

## File Structure

**Backend — new**
| File | Responsibility |
|---|---|
| `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs` | Aggregate root. `static Create(...)`, `AddItem(...)`. |
| `src/Negosio.Domain/Entities/Delivery/DeliveryReceiptItem.cs` | Snapshotted line. |
| `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs` | `DeliveryReceiptDto`, `DeliveryReceiptItemDto`, `CreateDeliveryReceiptRequest`, `CreateDeliveryReceiptItemInput`, `IDeliveryReceiptService`. |
| `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` | Create-or-get from sale, get by id, branch scoping, number allocation, snapshot. |
| `src/Negosio.Application/Delivery/DeliveryReceiptValidators.cs` | `CreateDeliveryReceiptRequestValidator`. |
| `src/Negosio.Infrastructure/Persistence/Configurations/DeliveryReceiptConfiguration.cs` | Tables + indexes. |
| `src/Negosio.Api/Controllers/DeliveryReceiptsController.cs` | `GET /api/delivery-receipts/{id}`. |
| `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/*_AddDeliveryReceipts.cs` | Generated. |

**Backend — modified**
| File | Change |
|---|---|
| `src/Negosio.Domain/Enums/DocumentNumberType.cs` | `DeliveryReceipt = 5` — **already added in Plan A Task 3**; if Plan B is executed standalone, add it here. |
| `src/Negosio.Application/Common/DocumentNumberService.cs` | `switch` arm: `DocumentNumberType.DeliveryReceipt => $"DR-{value:D6}"`. |
| `src/Negosio.Application/Abstractions/ITenantDbContext.cs` + `.../Tenant/TenantDbContext.cs` | `DbSet<DeliveryReceipt>`, `DbSet<DeliveryReceiptItem>`. |
| `src/Negosio.Api/Controllers/SalesController.cs` | `POST /api/sales/{id}/delivery-receipt`, `GET /api/sales/{id}/delivery-receipt`. |
| `src/Negosio.Application/DependencyInjection.cs` | Register `IDeliveryReceiptService`. |

**Frontend — new**
| File | Responsibility |
|---|---|
| `web/negosio-web/src/api/deliveryReceipts.ts` | `deliveryReceiptsApi.getForSale(saleId)`, `.createForSale(saleId, body)`, `.get(id)`. |
| `web/negosio-web/src/components/receipt/deliveryReceiptStyles.ts` | A4/Letter print CSS (margins, item table, signature grid). |
| `web/negosio-web/src/pages/DeliveryReceiptPage.tsx` | Route `/delivery-receipts/:id`, `?print=1` auto-print. |
| `web/negosio-web/src/components/sales/CreateDeliveryReceiptModal.tsx` | Recipient/address/contact/notes form + per-line qty; `POST` then navigate to the print page. |

**Frontend — modified**
| File | Change |
|---|---|
| `web/negosio-web/src/api/types.ts` | `DeliveryReceiptDto`, `DeliveryReceiptItemDto`, `CreateDeliveryReceiptRequest`. |
| `web/negosio-web/src/pages/SaleDetailPage.tsx` | "Print delivery receipt" / "View delivery receipt" action + modal wiring. |
| `web/negosio-web/src/App.tsx` | `/delivery-receipts/:id` route (like `/sales/:id/receipt` — no capability wrapper; service enforces branch). |

---

## Task 1: `DocumentNumberType.DeliveryReceipt` + format arm

**Files:**
- Modify: `src/Negosio.Domain/Enums/DocumentNumberType.cs` (skip if Plan A already added it), `src/Negosio.Application/Common/DocumentNumberService.cs`
- Test: `tests/Negosio.UnitTests/Common/DocumentNumberServiceTests.cs` (add case) — if no unit test exists, add an integration assertion in Task 5 instead.

**Interfaces:**
- Produces: `DocumentNumberType.DeliveryReceipt = 5`; `NextAsync(..., DeliveryReceipt, ...)` returns `"DR-000001"` (`$"DR-{value:D6}"`).

- [ ] **Step 1: Failing test** (unit, if the test file exists):

```csharp
[Fact]
public async Task DeliveryReceipt_numbers_are_DR_prefixed_six_digits()
{
    // arrange a counter at 0 for (tenant, branch, DeliveryReceipt) — reuse the file's existing arrange helper
    var n = await Service.NextAsync(Tenant, Branch, DocumentNumberType.DeliveryReceipt, "MAIN", default);
    n.Should().Be("DR-000001");
}
```

If `DocumentNumberServiceTests` uses a real DB fixture, mirror its arrange exactly; otherwise defer this assertion to Task 5's integration test (`dr.Number.Should().Be("DR-000001")`).

- [ ] **Step 2: Run, verify FAIL** (or note "deferred to Task 5").
- [ ] **Step 3: Add the enum value** (if not present) with a doc-comment line: `DeliveryReceipt = 5` — "Its own branch-scoped sequence, formatted `DR-000001`."
- [ ] **Step 4: Add the switch arm** in `DocumentNumberService.NextAsync`:

```csharp
return type switch
{
    DocumentNumberType.Sale or DocumentNumberType.Return => $"{value:D7}",
    DocumentNumberType.DeliveryReceipt => $"DR-{value:D6}",
    DocumentNumberType.PurchaseOrder => $"PO-{branchCode}-{value:D6}",
    DocumentNumberType.StockTransfer => $"TRN-{branchCode}-{value:D6}",
    _ => $"DOC-{branchCode}-{value:D6}"
};
```

- [ ] **Step 5: Run test / build.**
- [ ] **Step 6: Commit** — `git commit -am "feat(delivery): DR- document number type"`

---

## Task 2: `DeliveryReceipt` + `DeliveryReceiptItem` entities

**Files:**
- Create: `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs`, `DeliveryReceiptItem.cs`
- Test: `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs`

**Interfaces:**
- Produces:
  - `DeliveryReceipt : Entity` — props: `TenantId`, `BranchId`, `Number`, `SaleId : Guid?`, `RelatedSaleNumber : string?`, `RecipientName`, `DeliveryAddress`, `ContactNumber : string?`, `DeliveryNotes : string?`, `PreparedByUserId`, `PreparedByNameSnapshot`, `IReadOnlyCollection<DeliveryReceiptItem> Items`.
  - `static DeliveryReceipt Create(Guid tenantId, Guid branchId, string number, Guid? saleId, string? relatedSaleNumber, string recipientName, string deliveryAddress, string? contactNumber, string? deliveryNotes, Guid preparedByUserId, string preparedByNameSnapshot)` — trims strings, requires `recipientName` + `deliveryAddress` non-empty, requires `number` non-empty.
  - `DeliveryReceiptItem AddItem(string productNameSnapshot, string? variantNameSnapshot, decimal quantity, decimal? unitPrice)` — `quantity > 0`.
  - `DeliveryReceiptItem : Entity` — `TenantId`, `DeliveryReceiptId`, `ProductNameSnapshot`, `VariantNameSnapshot : string?`, `Quantity`, `UnitPrice : decimal?`.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class DeliveryReceiptEntityTests
{
    private static DeliveryReceipt Make() => DeliveryReceipt.Create(
        Guid.NewGuid(), Guid.NewGuid(), "DR-000001", Guid.NewGuid(), "0000042",
        "  Juan Dela Cruz ", " 123 Ayala Ave, Makati ", " 0917 111 2222 ", "  Leave at guardhouse ",
        Guid.NewGuid(), "Cashier One");

    [Fact]
    public void Create_trims_and_keeps_snapshot_fields()
    {
        var dr = Make();
        dr.Number.Should().Be("DR-000001");
        dr.RecipientName.Should().Be("Juan Dela Cruz");
        dr.DeliveryAddress.Should().Be("123 Ayala Ave, Makati");
        dr.ContactNumber.Should().Be("0917 111 2222");
        dr.RelatedSaleNumber.Should().Be("0000042");
        dr.PreparedByNameSnapshot.Should().Be("Cashier One");
    }

    [Fact]
    public void Create_requires_recipient_and_address()
    {
        var act = () => DeliveryReceipt.Create(Guid.NewGuid(), Guid.NewGuid(), "DR-000001", null, null,
            "   ", "addr", null, null, Guid.NewGuid(), "x");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddItem_snapshots_line_and_rejects_zero_qty()
    {
        var dr = Make();
        dr.AddItem("Coke 1.5L", null, 3m, 85m);
        dr.Items.Should().ContainSingle();
        dr.Items.Single().UnitPrice.Should().Be(85m);

        var act = () => dr.AddItem("Bad", null, 0m, null);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** both entities following the `SaleReturn` / `SaleReturnItem` pattern (private ctor for EF, `List<T>` field + `AsReadOnly()`, static `Create`, `internal` item ctor).
- [ ] **Step 4: Run tests, verify PASS.**
- [ ] **Step 5: Commit** — `git commit -am "feat(delivery): DeliveryReceipt aggregate"`

---

## Task 3: EF configuration + migration

**Files:**
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/DeliveryReceiptConfiguration.cs`
- Modify: `src/Negosio.Application/Abstractions/ITenantDbContext.cs`, `.../Tenant/TenantDbContext.cs`
- Generate: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/*_AddDeliveryReceipts.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptSchemaTests.cs` (smoke: insert a DR + 2 items, read back)

- [ ] **Step 1: Add DbSets** to the interface + context (`Set<DeliveryReceipt>()`, `Set<DeliveryReceiptItem>()`).
- [ ] **Step 2: Write the config**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class DeliveryReceiptConfiguration : IEntityTypeConfiguration<DeliveryReceipt>
{
    public void Configure(EntityTypeBuilder<DeliveryReceipt> b)
    {
        b.ToTable("DeliveryReceipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.BranchId).IsRequired();
        b.Property(x => x.Number).IsRequired().HasMaxLength(30).IsUnicode(false);
        b.Property(x => x.SaleId);
        b.Property(x => x.RelatedSaleNumber).HasMaxLength(30).IsUnicode(false);
        b.Property(x => x.RecipientName).IsRequired().HasMaxLength(120);
        b.Property(x => x.DeliveryAddress).IsRequired().HasMaxLength(300);
        b.Property(x => x.ContactNumber).HasMaxLength(40);
        b.Property(x => x.DeliveryNotes).HasMaxLength(1000);
        b.Property(x => x.PreparedByUserId).IsRequired();
        b.Property(x => x.PreparedByNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.DeliveryReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Number })
            .IsUnique().HasDatabaseName("IX_DeliveryReceipts_TenantId_BranchId_Number");
        // NON-unique — one-per-sale is a service rule, not a schema rule (multiple deliveries later)
        b.HasIndex(x => new { x.TenantId, x.SaleId })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_SaleId");
    }
}

public sealed class DeliveryReceiptItemConfiguration : IEntityTypeConfiguration<DeliveryReceiptItem>
{
    public void Configure(EntityTypeBuilder<DeliveryReceiptItem> b)
    {
        b.ToTable("DeliveryReceiptItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.DeliveryReceiptId).IsRequired();
        b.Property(x => x.ProductNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.VariantNameSnapshot).HasMaxLength(200);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.UnitPrice).HasPrecision(18, 2);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.DeliveryReceiptId })
            .HasDatabaseName("IX_DeliveryReceiptItems_TenantId_DeliveryReceiptId");
    }
}
```

- [ ] **Step 3: Generate the migration** (command in Global Constraints). Eyeball `Up()`: two `CreateTable`, three indexes, one FK to `Sales`. No data statements.
- [ ] **Step 4: Schema smoke test** (mirror Plan A's `ReceiptSettingsSchemaTests` structure — `InScopeAsync`, add DR + items, `SaveChangesAsync`, read back count).
- [ ] **Step 5: Run** `dotnet test --filter "FullyQualifiedName~DeliveryReceiptSchemaTests"` → PASS.
- [ ] **Step 6: Commit** — `git commit -am "feat(delivery): DeliveryReceipt EF config + migration"`

---

## Task 4: `DeliveryReceiptContracts` + validator

**Files:**
- Create: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs`, `DeliveryReceiptValidators.cs`
- Test: `tests/Negosio.UnitTests/Delivery/CreateDeliveryReceiptRequestValidatorTests.cs`

**Interfaces:**
- Produces:
  - `sealed record CreateDeliveryReceiptItemInput(Guid SaleItemId, decimal Quantity)`
  - `sealed record CreateDeliveryReceiptRequest(string RecipientName, string DeliveryAddress, string? ContactNumber, string? DeliveryNotes, IReadOnlyList<CreateDeliveryReceiptItemInput>? Items)` — `Items = null` ⇒ "use all sale lines at full quantity".
  - `sealed record DeliveryReceiptItemDto(string ProductName, string? VariantName, decimal Quantity, decimal? UnitPrice, decimal? Amount)` — `UnitPrice`/`Amount` are `null` when `DeliveryShowPrices` is off.
  - `sealed record DeliveryReceiptDto(Guid Id, string Number, DateTime CreatedAtUtc, string? RelatedSaleNumber, string BranchName, string RecipientName, string DeliveryAddress, string? ContactNumber, string? DeliveryNotes, string PreparedByName, IReadOnlyList<DeliveryReceiptItemDto> Items, string? HeaderText, string? FooterText, string BusinessName, string? BusinessAddress, string? BusinessContactNumber, string? TaxId, bool ShowPrices, bool ShowRelatedSaleNumber, bool ShowContactNumber, bool ShowSignatureFields)`
  - `interface IDeliveryReceiptService { Task<DeliveryReceiptDto> CreateOrGetForSaleAsync(Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default); Task<DeliveryReceiptDto?> GetForSaleAsync(Guid saleId, CancellationToken ct = default); Task<DeliveryReceiptDto> GetAsync(Guid id, CancellationToken ct = default); }`
  - `CreateDeliveryReceiptRequestValidator`: `RecipientName` required ≤ 120; `DeliveryAddress` required ≤ 300; `ContactNumber` ≤ 40; `DeliveryNotes` ≤ 1000; each item `Quantity > 0`.

- [ ] **Step 1: Failing validator tests** (mirror Plan A Task 2 style).
- [ ] **Step 2: Create contracts + validator.**
- [ ] **Step 3: Run tests, verify PASS.**
- [ ] **Step 4: Commit** — `git commit -am "feat(delivery): delivery-receipt contracts + validator"`

---

## Task 5: `DeliveryReceiptService`

**Files:**
- Create: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs`
- Modify: `src/Negosio.Application/DependencyInjection.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptTests.cs`

**Interfaces:**
- Consumes: `ITenantDbContext`, `ICurrentUser`, `IValidator<CreateDeliveryReceiptRequest>`, `IDocumentNumberService`, `IBranchAccessResolver`, `IReceiptSettingsResolver` (Plan A), `TenantProfile`, `Branch`, `User` (for `PreparedByNameSnapshot`).

**Behaviour:**
- `CreateOrGetForSaleAsync(saleId, request)`:
  1. `RequireTenant()`. Load the sale with `Items`; 404 if missing. `GuardSaleBranch` (branch-scoped user on another branch → 404) — copy the helper from `ReceiptService`/`ReturnService`.
  2. If a `DeliveryReceipt` already exists with `TenantId == t && SaleId == saleId` → return `await GetAsync(existing.Id)` (HTTP 200, no allocation). **This is the one-per-sale enforcement.**
  3. Validate the request.
  4. Resolve the item set: `request.Items` mapped to `(SaleItem, qty)` pairs (validate each `SaleItemId` is on the sale and `qty <= saleItem.Quantity`); or, when `request.Items == null`, every sale item at full `Quantity`.
  5. `await using var tx = await _db.Database.BeginTransactionAsync(ct);`
  6. `var number = await _documentNumbers.NextAsync(t, sale.BranchId, DocumentNumberType.DeliveryReceipt, branch.Code, ct);`
  7. `var preparedByName = <User.FirstName + " " + LastName for _currentUser.UserId>;`
  8. `DeliveryReceipt.Create(t, sale.BranchId, number, sale.Id, sale.SaleNumber, request.RecipientName, request.DeliveryAddress, request.ContactNumber, request.DeliveryNotes, _currentUser.UserId, preparedByName)`; for each pair `dr.AddItem(saleItem.ProductNameSnapshot, saleItem.VariantNameSnapshot, qty, saleItem.UnitPrice)`.
  9. `_db.DeliveryReceipts.Add(dr); await _db.SaveChangesAsync(ct); await tx.CommitAsync(ct);`
  10. Return `await GetAsync(dr.Id, ct)`.
- `GetForSaleAsync(saleId)` → branch-guarded lookup, `null` if none.
- `GetAsync(id)`:
  1. Load DR with `Items`; 404 if missing; branch-guard on `dr.BranchId`.
  2. `var settings = await _receiptSettings.ResolveAsync(dr.BranchId, ct);` → use the **Delivery** fields.
  3. Load `TenantProfile` (name/contact/taxId) + `Branch` (name/address/contact).
  4. Map items: `Amount = qty * unitPrice` (rounded, `Money.Round` if available); when `!settings.DeliveryShowPrices` → `UnitPrice = null, Amount = null`.
  5. Populate `HeaderText = settings.DeliveryHeaderText`, `FooterText = settings.DeliveryFooterText`, the `Show*` flags, business fields.

- [ ] **Step 1: Failing integration tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptTests : IntegrationTest
{
    public DeliveryReceiptTests(NegosioApiFactory factory) : base(factory) { }

    private static CreateDeliveryReceiptRequest Req(IReadOnlyList<CreateDeliveryReceiptItemInput>? items = null) =>
        new("Juan Dela Cruz", "123 Ayala Ave, Makati", "0917 111 2222", "Leave at guardhouse", items);

    [Fact]                                                        // acceptance 14, 15, 16, 17
    public async Task Create_from_sale_persists_with_unique_DR_number_and_snapshots()
    {
        var scene = await ArrangeSaleAsync(qty: 2m, price: 50m);   // reuse Plan A / Checkout arrange helpers
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await created.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        dr.Number.Should().Be("DR-000001");
        dr.RecipientName.Should().Be("Juan Dela Cruz");
        dr.RelatedSaleNumber.Should().Be(scene.SaleNumber);
        dr.Items.Should().ContainSingle();
        dr.Items[0].Quantity.Should().Be(2m);
    }

    [Fact]                                                        // acceptance 20
    public async Task Second_post_for_same_sale_returns_the_same_document()
    {
        var scene = await ArrangeSaleAsync();
        var first = await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options);
        var second = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req());
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondDto = (await second.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        secondDto.Id.Should().Be(first!.Id);
        secondDto.Number.Should().Be(first.Number);
    }

    [Fact]                                                        // acceptance 18
    public async Task Prices_are_hidden_when_DeliveryShowPrices_is_off()
    {
        var scene = await ArrangeSaleAsync(price: 50m);
        await Client.PutAsJsonAsync("/api/settings/receipts", new UpdateReceiptSettingsRequest(
            ReceiptWidth.Mm80, null, null, true, true, true, true, true,
            "DELIVERY RECEIPT", "Please inspect on receipt",
            DeliveryShowPrices: false, DeliveryShowRelatedSaleNumber: true,
            DeliveryShowContactNumber: true, DeliveryShowSignatureFields: true));

        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        dr.ShowPrices.Should().BeFalse();
        dr.Items[0].UnitPrice.Should().BeNull();
        dr.Items[0].Amount.Should().BeNull();
        dr.HeaderText.Should().Be("DELIVERY RECEIPT");
        dr.FooterText.Should().Be("Please inspect on receipt");
    }

    [Fact]                                                        // acceptance: reprint stable
    public async Task Get_by_id_returns_same_number_after_catalog_rename()
    {
        var scene = await ArrangeSaleAsync();
        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        // rename the product, then re-fetch the DR
        await RenameProductAsync(scene.ProductId, "Totally Different Name");
        var again = await Client.GetFromJsonAsync<DeliveryReceiptDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        again!.Number.Should().Be(dr.Number);
        again.Items[0].ProductName.Should().Be(dr.Items[0].ProductName);   // snapshot, not the new name
    }

    [Fact]
    public async Task Branch_scoped_user_cannot_read_another_branchs_DR()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var scene = await ArrangeSaleAsync();
        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        var other = await CreateBranchAsync("BGC", "BGC");
        Authorize(await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, other.Id));
        (await Client.GetAsync($"/api/delivery-receipts/{dr.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

> `ArrangeSaleAsync` returning `SaleId / SaleNumber / ProductId` and `RenameProductAsync` may need to be added to `IntegrationTest` or the test class — build on the existing Checkout/Catalog helpers (`SeedStockedProductAsync`, `CheckoutOkAsync`). Keep them in the test file if they're delivery-specific.

- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement the service**; register `services.AddScoped<IDeliveryReceiptService, DeliveryReceiptService>();` under a `// Delivery receipts` block.
- [ ] **Step 4: Wire the SalesController endpoints** (Task 6 — but you can add them now to make the tests runnable). Split into Task 6 only if you want a separate review gate; otherwise fold here.
- [ ] **Step 5: Iterate to green; full `dotnet test`.**
- [ ] **Step 6: Commit** — `git commit -am "feat(delivery): DeliveryReceiptService (create-or-get from sale, snapshot, DR number)"`

---

## Task 6: Controllers

**Files:**
- Modify: `src/Negosio.Api/Controllers/SalesController.cs`
- Create: `src/Negosio.Api/Controllers/DeliveryReceiptsController.cs`
- Test: covered by Task 5's HTTP-level tests.

- [ ] **Step 1: `SalesController`** — inject `IDeliveryReceiptService`; add:

```csharp
[HttpGet("{id:guid}/delivery-receipt")]
[ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status200OK)]
public async Task<ActionResult<DeliveryReceiptDto>> GetDeliveryReceipt(Guid id, CancellationToken ct)
{
    var dr = await _deliveryReceipts.GetForSaleAsync(id, ct);
    return dr is null ? NotFound() : Ok(dr);
}

[HttpPost("{id:guid}/delivery-receipt")]
[ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status201Created)]
[ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status200OK)]
public async Task<ActionResult<DeliveryReceiptDto>> CreateDeliveryReceipt(
    Guid id, [FromBody] CreateDeliveryReceiptRequest request, CancellationToken ct)
{
    var existing = await _deliveryReceipts.GetForSaleAsync(id, ct);
    var dr = await _deliveryReceipts.CreateOrGetForSaleAsync(id, request, ct);
    return existing is null
        ? CreatedAtAction(nameof(GetDeliveryReceipt), new { id }, dr)
        : Ok(dr);
}
```

The class-level `[Authorize(Policy = SalesView)]` already limits this to Owner/Admin/Manager/Cashier — matches the spec.

- [ ] **Step 2: `DeliveryReceiptsController`**

```csharp
[ApiController]
[Authorize(Policy = AuthorizationPolicies.SalesView)]
[Route("api/delivery-receipts")]
public sealed class DeliveryReceiptsController : ControllerBase
{
    private readonly IDeliveryReceiptService _service;
    public DeliveryReceiptsController(IDeliveryReceiptService service) => _service = service;

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryReceiptDto>> Get(Guid id, CancellationToken ct)
        => Ok(await _service.GetAsync(id, ct));
}
```

- [ ] **Step 3: Run Task 5's tests** (now including the `Created`/`OK` split assertions) → green. Full `dotnet test`.
- [ ] **Step 4: Commit** — `git commit -am "feat(delivery): delivery-receipt endpoints"`

---

## Task 7: Frontend — API, types, A4 print page

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`, `web/negosio-web/src/App.tsx`
- Create: `web/negosio-web/src/api/deliveryReceipts.ts`, `web/negosio-web/src/components/receipt/deliveryReceiptStyles.ts`, `web/negosio-web/src/pages/DeliveryReceiptPage.tsx`

**Interfaces:**
- `types.ts`: `DeliveryReceiptItemDto { productName: string; variantName: string | null; quantity: number; unitPrice: number | null; amount: number | null }`; `DeliveryReceiptDto { id; number; createdAtUtc; relatedSaleNumber: string | null; branchName; recipientName; deliveryAddress; contactNumber: string | null; deliveryNotes: string | null; preparedByName; items: DeliveryReceiptItemDto[]; headerText: string | null; footerText: string | null; businessName; businessAddress: string | null; businessContactNumber: string | null; taxId: string | null; showPrices; showRelatedSaleNumber; showContactNumber; showSignatureFields }`; `CreateDeliveryReceiptRequest { recipientName; deliveryAddress; contactNumber?; deliveryNotes?; items?: { saleItemId: string; quantity: number }[] }`.
- `deliveryReceiptsApi.getForSale(saleId)` (`GET /api/sales/{id}/delivery-receipt`, returns `DeliveryReceiptDto | null` — treat 404 as `null`), `.createForSale(saleId, body)`, `.get(id)`.

- [ ] **Step 1:** Types + `deliveryReceipts.ts`.
- [ ] **Step 2:** `deliveryReceiptStyles.ts` — A4 CSS: `.dr { width: 190mm; margin: 0 auto; font: 12px/1.5 system-ui; }`, a bordered items `table`, a `.dr-signatures` 3-column grid with `border-top` ruled lines and labels (Prepared by / Delivered by / Received by), `@media print { @page { size: A4; margin: 12mm; } .dr-actions { display:none } }`. Keep it self-contained (inline `<style>` like `ReceiptPage`).
- [ ] **Step 3:** `DeliveryReceiptPage.tsx` — route param `:id`, `useQuery(['delivery-receipts', id], () => deliveryReceiptsApi.get(id))`, `?print=1` → `window.print()` once loaded (copy the `printedRef` pattern from `ReceiptPage`). Layout:
  - `<ReceiptHeader headerText={d.headerText || 'DELIVERY RECEIPT'} business={{...}} />` — note: for the DR, when `headerText` is null show the business block **and** a `DELIVERY RECEIPT` title line.
  - Document details: DR #, date, `relatedSaleNumber` (only if `d.showRelatedSaleNumber`), branch.
  - Recipient / address / `contactNumber` (only if `d.showContactNumber`).
  - Items table: Qty · Product (+variant) · Unit price + Amount columns only when `d.showPrices` (and values are non-null).
  - Delivery notes block when present.
  - Signature grid when `d.showSignatureFields`.
  - `<ReceiptFooter footerText={d.footerText} />` — but for the DR a null footer should render nothing (pass a `variant="delivery"` prop or just `d.footerText && <ReceiptFooter .../>`).
- [ ] **Step 4:** `App.tsx` — `{ path: '/delivery-receipts/:id', element: <DeliveryReceiptPage /> }` in `protectedRoutes` (no `RequireCapability` — server enforces branch; any `sales:view` role may print).
- [ ] **Step 5:** `npx tsc --noEmit` + `npm run build` clean.
- [ ] **Step 6: Commit** — `git commit -am "feat(web): delivery receipt A4 print page"`

---

## Task 8: Frontend — create-from-Sale flow

**Files:**
- Create: `web/negosio-web/src/components/sales/CreateDeliveryReceiptModal.tsx`
- Modify: `web/negosio-web/src/pages/SaleDetailPage.tsx`

**Behaviour:**
- On `SaleDetailPage`, add a query: `useQuery(['sales', id, 'delivery-receipt'], () => deliveryReceiptsApi.getForSale(id))`.
- Action button in the existing button row (next to "Print receipt"):
  - DR exists → **"View delivery receipt"** → `window.open('/delivery-receipts/' + dr.id + '?print=1', '_blank', 'noopener')`.
  - No DR, and sale status is `Completed` / `PartiallyRefunded` / `Refunded` (a voided sale gets no DR) → **"Print delivery receipt"** → opens `CreateDeliveryReceiptModal`.
  - Show for any `sales:view` role (no extra capability).
- `CreateDeliveryReceiptModal`:
  - Fields: Recipient name (required), Delivery address (required, textarea), Contact number, Delivery notes (textarea).
  - Items: list the sale's items with an editable quantity per line (default = full qty; min 0 — a 0 excludes the line). Build `items: [{ saleItemId, quantity }]` (omit lines with qty 0; if all lines full, send `items: undefined` to keep it simple).
  - Submit → `deliveryReceiptsApi.createForSale(id, body)` → on success invalidate `['sales', id, 'delivery-receipt']`, close modal, `window.open('/delivery-receipts/' + dr.id + '?print=1', ...)`.
  - Error handling: 400 → field errors via `fieldErrorsFrom`; other → toast.
- Reuse `Modal`, `TextField`, `TextArea` (Plan A), `Button`, `useToast`.

- [ ] **Step 1:** Build `CreateDeliveryReceiptModal`.
- [ ] **Step 2:** Wire into `SaleDetailPage` (query + button + modal state).
- [ ] **Step 3:** `npx tsc --noEmit` + `npm run build` clean.
- [ ] **Step 4: Commit** — `git commit -am "feat(web): generate delivery receipt from sale detail"`

---

## Task 9: Live verification + docs

**Files:**
- Modify: `docs/handover.md`, `C:\Users\Ace\Desktop\negosio-status.md`

- [ ] **Step 1:** Servers up (`:5170` / `:5173`). Seeded accounts as in Plan A.
- [ ] **Step 2:** Headless-Chrome pass:
  - Owner: `/settings/receipts` → Delivery tab → set header "DELIVERY RECEIPT", footer "Received in good order", turn **off** "Show prices" → save.
  - Open a completed sale → **Print delivery receipt** → fill recipient "Maria Santos", address, contact, notes → submit → A4 page opens, auto-print dialog. Verify: DR-000001, related sale #, item quantities match the sale, **no price columns**, signature lines present, footer text applied. (acceptance 14, 16, 17, 18, 19)
  - Back on the sale → button now says **"View delivery receipt"** → opens the **same** DR-000001 (acceptance 20).
  - Turn "Show prices" back on → create a DR for a *different* sale → prices now shown. First sale's DR still shows no prices (settings resolved at... note: settings are resolved at **GET** time, so toggling later changes the reprint — that matches the spec's "reprint uses current saved settings" for the *sales* receipt; for the DR the spec says reprint = same document identity/number, but header/footer/toggles are still live settings. Confirm this is acceptable in the handover; the DR *data* (items, recipient, number) is immutable, the *presentation* follows current settings.)
  - Manager (BGC): can create a DR for a BGC sale; cannot open `/delivery-receipts/{id}` of a main-branch DR (redirect/404 view).
  - Void a sale → no delivery-receipt button (or disabled).
  - Zero console errors.
- [ ] **Step 3:** `dotnet test` full green; `npx tsc --noEmit` + `npm run build` clean.
- [ ] **Step 4:** Update `docs/handover.md` (new "Delivery Receipt (Plan B)" section — the aggregate, DR numbering, one-per-sale-is-a-service-rule, presentation-follows-live-settings-but-data-is-immutable, where it's generated) and `negosio-status.md`. Note branch unmerged pending review; Phase D items still deferred.
- [ ] **Step 5: Commit** — `git commit -am "docs: Delivery Receipt (Plan B) — handover + status"`

---

## Self-Review (completed by plan author)

**Spec coverage:**
- §4.2 `DeliveryReceipt`/`DeliveryReceiptItem`, snapshots, non-unique `SaleId`, one-per-sale service rule → Tasks 2, 3, 5. ✓
- §4.3 `DocumentNumberType.DeliveryReceipt` + `DR-{value:D6}` → Task 1. ✓
- §6.2 endpoints (`POST`/`GET` on sale, `GET /api/delivery-receipts/{id}`), `SalesView` + service branch check, idempotent create → Tasks 5, 6. ✓
- §7.3 A4 page, sections, `?print=1`, price columns gated, signature grid, configurable header/footer → Task 7. ✓
- §7.4 "Print delivery receipt" on Sale detail, becomes "View..." after creation, no POS button → Task 8. ✓
- §12 acceptance 14–20 → mapped inline in Task 5 (14,15,16,17,18,20) + Task 9 (19, live 20, void-sale). ✓
- Delivery header/footer/toggles sourced from Plan A `ReceiptSettings` (delivery fields) → Task 5 Step (GetAsync uses `IReceiptSettingsResolver`). ✓
- No Customer / no tracking / no digital signature / no POS button → enforced by scope; signature = blank lines (Task 7 Step 3). ✓

**Placeholder scan:** `ArrangeSaleAsync` / `RenameProductAsync` flagged as "build on existing helpers" — acceptable (they compose documented existing helpers; exact names depend on the current `IntegrationTest.cs`). `Money.Round` — use if present, else `Math.Round(x, 2, MidpointRounding.AwayFromZero)`. No TODO/TBD.

**Type consistency:** `DeliveryReceiptDto` / `DeliveryReceiptItemDto` field names identical across C# record, TS interface (Task 7), and test assertions (Task 5). `CreateDeliveryReceiptRequest.Items` optional in both. `IDeliveryReceiptService` three methods used by both controllers (Task 6) and no one else. `DocumentNumberType.DeliveryReceipt` value `5` consistent with Plan A.

**One flagged decision for the user** (surface in Task 9 handover, not blocking): a DR's **data** is immutable (number, items, recipient) but its **presentation** (header/footer/price visibility) is re-resolved from live `ReceiptSettings` on every GET/reprint. This matches the spec's "reprint uses current saved receipt settings" for receipts generally and "reprint produces the same delivery document identity/number" for the DR — both hold. If the user wants the DR presentation *also* frozen at creation, that's a schema change (snapshot the settings onto the DR row) — note it, don't build it.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-09-delivery-receipt.md`.** Execute **after** Plan A. Same two options (subagent-driven recommended / inline).
