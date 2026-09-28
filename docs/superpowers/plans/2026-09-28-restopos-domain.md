# RestoPOS Domain + Migrations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the RestoPOS domain model (entities, EF configurations, one migration) with no service/application layer yet — Milestone 1 of the approved RestoPOS design, chosen as the first plan because it is the smallest slice that is independently testable end-to-end (a fully migrated, invariant-enforcing schema) before any business logic is layered on top.

**Architecture:** New entities under `src/Negosio.Domain/Entities/Resto/`, following this codebase's existing rich-domain-model convention exactly (private constructor + public static factory with validation, private setters, mutator methods calling `Touch()`, an `Entity` base class for `Id`/`CreatedAtUtc`/`UpdatedAtUtc`). New EF configurations under `src/Negosio.Infrastructure/Persistence/Configurations/`. Two small additions to existing entities (`Sale.Origin`, `Branch` service-type flags). One tenant-database migration at the end, once every entity/config task has landed.

**Tech Stack:** .NET 9, EF Core 9, SQL Server (LocalDB for tests), xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-09-28-restopos-design.md` (Sections 2 "Confirmed Code Findings" and 3 "Proposed Domain Model" are this plan's direct source; Section 10 "Unresolved Decisions" governs what NOT to build yet).

## Global Constraints

- Every new entity is `TenantId`-scoped (multi-tenant convention, every existing entity in this codebase carries it).
- **Namespace is flat, never nested by folder.** Every domain entity declares `namespace Negosio.Domain.Entities;` regardless of which subfolder it physically lives in — confirmed directly: `Sale.cs` (folder `Entities/Sales/`), `DeliveryReceipt.cs` (folder `Entities/Delivery/`), and `ProductVariant`/`Product` (folder `Entities/Catalog/`, confirmed via `SaleItemConfiguration.cs`'s unqualified reference to `ProductVariant`) all declare the same flat namespace. Every new entity in this plan lives under `src/Negosio.Domain/Entities/Resto/` on disk but must declare `namespace Negosio.Domain.Entities;`, identical to everything else — do not introduce a nested `Negosio.Domain.Entities.Resto` namespace.
- Follow the existing rich-domain-model style exactly: private parameterless constructor (for EF), private full constructor, a public static factory method that validates and constructs, private setters, mutator methods that call `Touch()` (inherited from `Entity`, `src/Negosio.Domain/Common/Entity.cs`). Domain-level invariant violations throw plain BCL exceptions (`ArgumentException`, `ArgumentOutOfRangeException`, `InvalidOperationException`) — never `BusinessRuleException`/`ConflictException`/etc., which belong to the application-service layer that does not exist yet in this plan.
- Money fields: `decimal`, EF `HasPrecision(18, 2)`. Quantity fields: `decimal`, EF `HasPrecision(18, 3)` — matching `SaleItemConfiguration.cs`'s existing precision choices exactly.
- Enum properties: `HasConversion<int>()` in the EF configuration, matching every existing enum-typed column in this codebase.
- Filtered unique indexes follow `RegisterSessionConfiguration.cs`'s exact style: `builder.HasIndex(...).IsUnique().HasFilter($"[Status] = {(int)SomeEnum.Value}").HasDatabaseName("IX_...")`.
- **Do not add any bill-level-discount field anywhere in this plan.** The design spec explicitly defers this (Section 10, Unresolved Decision #1) — line-level discount only.
- **No service/application layer, no authorization, no migrations-applied-at-runtime behavior in this plan.** Pure domain + EF mapping + one migration. Business rules that require reading sibling aggregates (e.g. "can't disable a station with active tickets," "can't cancel an order once a round is released") are explicitly deferred to the M2 plan and must NOT be implemented here even as a convenience — note them with a one-line comment where relevant instead.
- Migrations are generated with: `dotnet dotnet-ef migrations add <Name> --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb` (from `docs/superpowers/plans/../../../README.md`'s "EF Core migrations" section — run from the repo root). `dotnet-ef` is a pinned local tool; run `dotnet tool restore` first if the command isn't found.
- Unit tests for new entities go under `tests/Negosio.UnitTests/Resto/`, one test file per entity, matching the existing `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs` file-per-entity convention.

---

## Task 1: Resto enums

**Files:**
- Create: `src/Negosio.Domain/Enums/RestoServiceType.cs`
- Create: `src/Negosio.Domain/Enums/RestoOrderStatus.cs`
- Create: `src/Negosio.Domain/Enums/RestoOrderRoundStatus.cs`
- Create: `src/Negosio.Domain/Enums/RestoKitchenStatus.cs`
- Create: `src/Negosio.Domain/Enums/ModifierSelectionType.cs`
- Create: `src/Negosio.Domain/Enums/SaleOrigin.cs`

**Interfaces:**
- Produces: six enums every later task in this plan (and the M2 plan) consumes by exact name.

- [ ] **Step 1: Write the six enum files**

```csharp
// src/Negosio.Domain/Enums/RestoServiceType.cs
namespace Negosio.Domain.Enums;

/// <summary>Which of the two RestoPOS flows a <c>RestoOrder</c> follows. Fixed at creation, never
/// changed — see the design spec's Section 4 lifecycle rules.</summary>
public enum RestoServiceType
{
    PayAsYouOrder = 1,
    BillOut = 2,
}
```

```csharp
// src/Negosio.Domain/Enums/RestoOrderStatus.cs
namespace Negosio.Domain.Enums;

public enum RestoOrderStatus
{
    Open = 1,
    Settled = 2,
    Cancelled = 3,
}
```

```csharp
// src/Negosio.Domain/Enums/RestoOrderRoundStatus.cs
namespace Negosio.Domain.Enums;

/// <summary>Deliberately thin — a round only tracks whether it has been sent at all. The real
/// kitchen-workflow state lives per item, in <see cref="RestoKitchenStatus"/>, so that one round can
/// span multiple stations without a separate ticket entity per station.</summary>
public enum RestoOrderRoundStatus
{
    Draft = 1,
    Released = 2,
    Voided = 3,
}
```

```csharp
// src/Negosio.Domain/Enums/RestoKitchenStatus.cs
namespace Negosio.Domain.Enums;

/// <summary><see cref="Pending"/> means "released, not yet acknowledged" — distinct from
/// <see cref="Acknowledged"/>, which requires an actual kitchen-side action. See design spec
/// Section 6.2.</summary>
public enum RestoKitchenStatus
{
    Pending = 1,
    Acknowledged = 2,
    Ready = 3,
    Served = 4,
}
```

```csharp
// src/Negosio.Domain/Enums/ModifierSelectionType.cs
namespace Negosio.Domain.Enums;

public enum ModifierSelectionType
{
    Single = 1,
    Multiple = 2,
}
```

```csharp
// src/Negosio.Domain/Enums/SaleOrigin.cs
namespace Negosio.Domain.Enums;

/// <summary>Set once at <see cref="Negosio.Domain.Entities.Sale"/> creation, never changed. The one
/// discriminator that lets <c>ReturnService</c> reject a return against a Resto sale — see design
/// spec Section 7.</summary>
public enum SaleOrigin
{
    Retail = 1,
    Resto = 2,
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Negosio.Domain`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Negosio.Domain/Enums/RestoServiceType.cs src/Negosio.Domain/Enums/RestoOrderStatus.cs src/Negosio.Domain/Enums/RestoOrderRoundStatus.cs src/Negosio.Domain/Enums/RestoKitchenStatus.cs src/Negosio.Domain/Enums/ModifierSelectionType.cs src/Negosio.Domain/Enums/SaleOrigin.cs
git commit -m "feat(resto): add RestoPOS domain enums"
```

---

## Task 2: `RestoStation`

**Files:**
- Create: `src/Negosio.Domain/Entities/Resto/RestoStation.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/RestoStationConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Resto/RestoStationTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `RestoStation.Create(tenantId, branchId, name) -> RestoStation`; `Rename(string name)`; `Disable()`; `Enable()`; properties `TenantId, BranchId, Name, IsActive`. Task 7 (`RestoOrderItem`) references `RestoStation.Id` and snapshots `Name` at add-time.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Negosio.UnitTests/Resto/RestoStationTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoStationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();

    [Fact]
    public void Create_sets_the_expected_fields_and_starts_active()
    {
        var station = RestoStation.Create(TenantId, BranchId, "Kitchen");

        station.TenantId.Should().Be(TenantId);
        station.BranchId.Should().Be(BranchId);
        station.Name.Should().Be("Kitchen");
        station.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_a_blank_name()
    {
        var act = () => RestoStation.Create(TenantId, BranchId, "   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rename_trims_and_updates_the_name()
    {
        var station = RestoStation.Create(TenantId, BranchId, "Kitchen");

        station.Rename("  Hot Kitchen  ");

        station.Name.Should().Be("Hot Kitchen");
    }

    [Fact]
    public void Disable_then_Enable_round_trips_IsActive()
    {
        var station = RestoStation.Create(TenantId, BranchId, "Bar");

        station.Disable();
        station.IsActive.Should().BeFalse();

        station.Enable();
        station.IsActive.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoStationTests"`
Expected: build error — `Negosio.Domain.Entities` / `RestoStation` do not exist yet.

- [ ] **Step 3: Implement `RestoStation`**

```csharp
// src/Negosio.Domain/Entities/Resto/RestoStation.cs
using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>A branch-configurable kitchen/bar station (Kitchen, Bar, Dessert, Grill, ...) that a menu
/// item routes to. Deliberately its own entity rather than a hardcoded enum or free string, so a
/// tenant can name their own stations without a code change — mirrors <c>Category</c>'s shape.
/// Branch-scoped, not tenant-wide: a physical station genuinely differs per branch, the same way a
/// <see cref="Register"/> does.
///
/// Disabling/renaming must never rewrite an existing <c>RestoOrderItem</c> — every item snapshots
/// both this station's <see cref="Id"/> and its <see cref="Name"/> at add-time (see
/// <c>RestoOrderItem.StationNameSnapshot</c>). The rule that a station cannot be disabled while it
/// still has non-terminal (unserved) items routed to it is a cross-aggregate check that belongs to a
/// future application service, not this entity — this class only flips <see cref="IsActive"/>.</summary>
public class RestoStation : Entity
{
    private RestoStation()
    {
        Name = string.Empty;
    }

    private RestoStation(Guid tenantId, Guid branchId, string name)
    {
        TenantId = tenantId;
        BranchId = branchId;
        Name = name;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static RestoStation Create(Guid tenantId, Guid branchId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Station name is required.", nameof(name));
        }

        return new RestoStation(tenantId, branchId, name.Trim());
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Station name is required.", nameof(name));
        }

        Name = name.Trim();
        Touch();
    }

    public void Disable()
    {
        IsActive = false;
        Touch();
    }

    public void Enable()
    {
        IsActive = true;
        Touch();
    }
}
```

- [ ] **Step 4: Run to confirm it passes**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoStationTests"`
Expected: 4/4 passing.

- [ ] **Step 5: Write the EF configuration**

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/RestoStationConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoStationConfiguration : IEntityTypeConfiguration<RestoStation>
{
    public void Configure(EntityTypeBuilder<RestoStation> builder)
    {
        builder.ToTable("RestoStations");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.BranchId).IsRequired();
        builder.Property(s => s.Name).IsRequired().HasMaxLength(100);
        builder.Property(s => s.IsActive).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.BranchId })
            .HasDatabaseName("IX_RestoStations_TenantId_BranchId");
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Domain/Entities/Resto/RestoStation.cs src/Negosio.Infrastructure/Persistence/Configurations/RestoStationConfiguration.cs tests/Negosio.UnitTests/Resto/RestoStationTests.cs
git commit -m "feat(resto): add RestoStation entity"
```

---

## Task 3: `RestoTable`

**Files:**
- Create: `src/Negosio.Domain/Entities/Resto/RestoTable.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/RestoTableConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Resto/RestoTableTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `RestoTable.Create(tenantId, branchId, name) -> RestoTable`; `Rename(string name)`; `Disable()`/`Enable()`; properties `TenantId, BranchId, Name, IsActive`. Task 5 (`RestoOrder`) references `RestoTable.Id`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Negosio.UnitTests/Resto/RestoTableTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoTableTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();

    [Fact]
    public void Create_sets_the_expected_fields_and_starts_active()
    {
        var table = RestoTable.Create(TenantId, BranchId, "Table 5");

        table.TenantId.Should().Be(TenantId);
        table.BranchId.Should().Be(BranchId);
        table.Name.Should().Be("Table 5");
        table.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_rejects_a_blank_name()
    {
        var act = () => RestoTable.Create(TenantId, BranchId, "");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Disable_then_Enable_round_trips_IsActive()
    {
        var table = RestoTable.Create(TenantId, BranchId, "Patio 2");

        table.Disable();
        table.IsActive.Should().BeFalse();

        table.Enable();
        table.IsActive.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoTableTests"`
Expected: build error.

- [ ] **Step 3: Implement `RestoTable`**

```csharp
// src/Negosio.Domain/Entities/Resto/RestoTable.cs
using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>A physical (or logical) dine-in table a Bill-Out <c>RestoOrder</c> is opened against.
/// Branch-scoped. Deliberately no position/layout fields — a floor-plan UI is out of scope for this
/// phase; this exists purely so a <c>RestoOrder</c> can hold a real foreign key instead of a free-text
/// label, which is what lets the filtered unique index on <c>RestoOrders(TableId) WHERE Status=Open</c>
/// (see <c>RestoOrderConfiguration</c>) actually prevent two concurrent visits on the same table.</summary>
public class RestoTable : Entity
{
    private RestoTable()
    {
        Name = string.Empty;
    }

    private RestoTable(Guid tenantId, Guid branchId, string name)
    {
        TenantId = tenantId;
        BranchId = branchId;
        Name = name;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static RestoTable Create(Guid tenantId, Guid branchId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name is required.", nameof(name));
        }

        return new RestoTable(tenantId, branchId, name.Trim());
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name is required.", nameof(name));
        }

        Name = name.Trim();
        Touch();
    }

    public void Disable()
    {
        IsActive = false;
        Touch();
    }

    public void Enable()
    {
        IsActive = true;
        Touch();
    }
}
```

- [ ] **Step 4: Run to confirm it passes**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoTableTests"`
Expected: 3/3 passing.

- [ ] **Step 5: Write the EF configuration**

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/RestoTableConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoTableConfiguration : IEntityTypeConfiguration<RestoTable>
{
    public void Configure(EntityTypeBuilder<RestoTable> builder)
    {
        builder.ToTable("RestoTables");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.BranchId).IsRequired();
        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.Property(t => t.IsActive).IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.BranchId })
            .HasDatabaseName("IX_RestoTables_TenantId_BranchId");
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Domain/Entities/Resto/RestoTable.cs src/Negosio.Infrastructure/Persistence/Configurations/RestoTableConfiguration.cs tests/Negosio.UnitTests/Resto/RestoTableTests.cs
git commit -m "feat(resto): add RestoTable entity"
```

---

## Task 4: `ModifierGroup`, `ModifierOption`, `ProductModifierGroup`

**Files:**
- Create: `src/Negosio.Domain/Entities/Resto/ModifierGroup.cs`
- Create: `src/Negosio.Domain/Entities/Resto/ModifierOption.cs`
- Create: `src/Negosio.Domain/Entities/Resto/ProductModifierGroup.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/ModifierGroupConfiguration.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/ProductModifierGroupConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Resto/ModifierGroupTests.cs`

**Interfaces:**
- Consumes: `ModifierSelectionType` (Task 1). References `Product` (existing entity, `src/Negosio.Domain/Entities/Catalog/Product.cs`) by id only — does not modify it.
- Produces: `ModifierGroup.Create(tenantId, name, selectionType) -> ModifierGroup`, owning a collection of `ModifierOption` via `AddOption(name, priceDelta) -> ModifierOption`; `ProductModifierGroup` as a plain join record. Task 8 (`RestoOrderItemModifier`) snapshots `ModifierOption.Name`/`PriceDelta` — it does not hold a live FK to `ModifierOption` beyond the snapshot fields, so this task's shape is otherwise self-contained.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Negosio.UnitTests/Resto/ModifierGroupTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class ModifierGroupTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void Create_sets_the_expected_fields_and_starts_active_with_no_options()
    {
        var group = ModifierGroup.Create(TenantId, "Add-ons", ModifierSelectionType.Multiple);

        group.TenantId.Should().Be(TenantId);
        group.Name.Should().Be("Add-ons");
        group.SelectionType.Should().Be(ModifierSelectionType.Multiple);
        group.IsActive.Should().BeTrue();
        group.Options.Should().BeEmpty();
    }

    [Fact]
    public void AddOption_appends_an_active_option_with_the_given_price_delta()
    {
        var group = ModifierGroup.Create(TenantId, "Add-ons", ModifierSelectionType.Multiple);

        var option = group.AddOption("Extra cheese", 20m);

        group.Options.Should().ContainSingle().Which.Should().BeSameAs(option);
        option.Name.Should().Be("Extra cheese");
        option.PriceDelta.Should().Be(20m);
        option.IsActive.Should().BeTrue();
    }

    [Fact]
    public void AddOption_rejects_a_negative_price_delta()
    {
        var group = ModifierGroup.Create(TenantId, "Add-ons", ModifierSelectionType.Single);

        var act = () => group.AddOption("Discount option", -5m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Disable_flips_IsActive_without_touching_existing_options()
    {
        var group = ModifierGroup.Create(TenantId, "Spice Level", ModifierSelectionType.Single);
        var option = group.AddOption("Mild", 0m);

        group.Disable();

        group.IsActive.Should().BeFalse();
        option.IsActive.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~ModifierGroupTests"`
Expected: build error.

- [ ] **Step 3: Implement `ModifierOption`**

```csharp
// src/Negosio.Domain/Entities/Resto/ModifierOption.cs
using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>One selectable choice within a <see cref="ModifierGroup"/> (e.g. "Extra cheese", "+20").
/// A menu-time definition — see <c>RestoOrderItemModifier</c> for the frozen snapshot taken when a
/// customer actually picks one.</summary>
public class ModifierOption : Entity
{
    private ModifierOption()
    {
        Name = string.Empty;
    }

    internal ModifierOption(Guid tenantId, Guid modifierGroupId, string name, decimal priceDelta)
    {
        TenantId = tenantId;
        ModifierGroupId = modifierGroupId;
        Name = name;
        PriceDelta = priceDelta;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public Guid ModifierGroupId { get; private set; }

    public string Name { get; private set; }

    public decimal PriceDelta { get; private set; }

    public bool IsActive { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Option name is required.", nameof(name));
        }

        Name = name.Trim();
        Touch();
    }

    public void Disable()
    {
        IsActive = false;
        Touch();
    }

    public void Enable()
    {
        IsActive = true;
        Touch();
    }
}
```

- [ ] **Step 4: Implement `ModifierGroup`**

```csharp
// src/Negosio.Domain/Entities/Resto/ModifierGroup.cs
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>A reusable, tenant-owned set of selectable options (e.g. "Add-ons", "Spice Level")
/// attachable to any number of products via <see cref="ProductModifierGroup"/>. Tenant-wide, not
/// branch-scoped — a menu's modifier definitions are shared across every branch that sells the
/// product, unlike <see cref="RestoStation"/>.</summary>
public class ModifierGroup : Entity
{
    private readonly List<ModifierOption> _options = new();

    private ModifierGroup()
    {
        Name = string.Empty;
    }

    private ModifierGroup(Guid tenantId, string name, ModifierSelectionType selectionType)
    {
        TenantId = tenantId;
        Name = name;
        SelectionType = selectionType;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public ModifierSelectionType SelectionType { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<ModifierOption> Options => _options.AsReadOnly();

    public static ModifierGroup Create(Guid tenantId, string name, ModifierSelectionType selectionType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Modifier group name is required.", nameof(name));
        }

        return new ModifierGroup(tenantId, name.Trim(), selectionType);
    }

    public ModifierOption AddOption(string name, decimal priceDelta)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Option name is required.", nameof(name));
        }

        if (priceDelta < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(priceDelta), "A modifier's price delta cannot be negative.");
        }

        var option = new ModifierOption(TenantId, Id, name.Trim(), priceDelta);
        _options.Add(option);
        return option;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Modifier group name is required.", nameof(name));
        }

        Name = name.Trim();
        Touch();
    }

    public void Disable()
    {
        IsActive = false;
        Touch();
    }

    public void Enable()
    {
        IsActive = true;
        Touch();
    }
}
```

- [ ] **Step 5: Implement `ProductModifierGroup`**

```csharp
// src/Negosio.Domain/Entities/Resto/ProductModifierGroup.cs
using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>Join row: which <see cref="ModifierGroup"/>s a given <c>Product</c> offers, and whether a
/// selection from that group is required for that specific product (e.g. "Spice Level" might be
/// required on a curry but not offered at all on a drink).</summary>
public class ProductModifierGroup : Entity
{
    private ProductModifierGroup()
    {
    }

    private ProductModifierGroup(Guid tenantId, Guid productId, Guid modifierGroupId, bool isRequired, int displayOrder)
    {
        TenantId = tenantId;
        ProductId = productId;
        ModifierGroupId = modifierGroupId;
        IsRequired = isRequired;
        DisplayOrder = displayOrder;
    }

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid ModifierGroupId { get; private set; }

    public bool IsRequired { get; private set; }

    public int DisplayOrder { get; private set; }

    public static ProductModifierGroup Create(Guid tenantId, Guid productId, Guid modifierGroupId, bool isRequired, int displayOrder) =>
        new(tenantId, productId, modifierGroupId, isRequired, displayOrder);
}
```

- [ ] **Step 6: Run to confirm the tests pass**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~ModifierGroupTests"`
Expected: 4/4 passing.

- [ ] **Step 7: Write the EF configurations**

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/ModifierGroupConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ModifierGroupConfiguration : IEntityTypeConfiguration<ModifierGroup>
{
    public void Configure(EntityTypeBuilder<ModifierGroup> builder)
    {
        builder.ToTable("ModifierGroups");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.TenantId).IsRequired();
        builder.Property(g => g.Name).IsRequired().HasMaxLength(100);
        builder.Property(g => g.SelectionType).IsRequired().HasConversion<int>();
        builder.Property(g => g.IsActive).IsRequired();
        builder.Property(g => g.CreatedAtUtc).IsRequired();
        builder.Property(g => g.UpdatedAtUtc).IsRequired();

        builder.HasMany(g => g.Options)
            .WithOne()
            .HasForeignKey(o => o.ModifierGroupId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(g => g.Options).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(g => g.TenantId).HasDatabaseName("IX_ModifierGroups_TenantId");
    }
}

public sealed class ModifierOptionConfiguration : IEntityTypeConfiguration<ModifierOption>
{
    public void Configure(EntityTypeBuilder<ModifierOption> builder)
    {
        builder.ToTable("ModifierOptions");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.ModifierGroupId).IsRequired();
        builder.Property(o => o.Name).IsRequired().HasMaxLength(100);
        builder.Property(o => o.PriceDelta).HasPrecision(18, 2);
        builder.Property(o => o.IsActive).IsRequired();
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();

        builder.HasIndex(o => new { o.TenantId, o.ModifierGroupId })
            .HasDatabaseName("IX_ModifierOptions_TenantId_ModifierGroupId");
    }
}
```

(Two configuration classes in one file, matching how `SaleConfiguration`-adjacent files sometimes group a parent+child pair — if code review prefers one class per file, split `ModifierOptionConfiguration` into its own `ModifierOptionConfiguration.cs`; both are auto-discovered by `ApplyConfigurationsFromAssembly` in `TenantDbContext.OnModelCreating` regardless of file layout.)

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/ProductModifierGroupConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ProductModifierGroupConfiguration : IEntityTypeConfiguration<ProductModifierGroup>
{
    public void Configure(EntityTypeBuilder<ProductModifierGroup> builder)
    {
        builder.ToTable("ProductModifierGroups");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.ModifierGroupId).IsRequired();
        builder.Property(x => x.IsRequired).IsRequired();
        builder.Property(x => x.DisplayOrder).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ModifierGroup>()
            .WithMany()
            .HasForeignKey(x => x.ModifierGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // One product offers a given modifier group at most once.
        builder.HasIndex(x => new { x.TenantId, x.ProductId, x.ModifierGroupId })
            .IsUnique().HasDatabaseName("IX_ProductModifierGroups_TenantId_ProductId_ModifierGroupId");
    }
}
```

`Product` lives at `src/Negosio.Domain/Entities/Catalog/Product.cs` but — confirmed by `SaleItemConfiguration.cs`'s own unqualified use of `ProductVariant` from the same folder — this codebase does not nest namespaces by folder; every domain entity is flatly `Negosio.Domain.Entities` regardless of its subfolder (`Sale.cs` under `Entities/Sales/`, `DeliveryReceipt.cs` under `Entities/Delivery/`, both confirmed the same way). `Product` and `ProductVariant` need no separate `using` beyond the one already in this file.

- [ ] **Step 8: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/Negosio.Domain/Entities/Resto/ModifierGroup.cs src/Negosio.Domain/Entities/Resto/ModifierOption.cs src/Negosio.Domain/Entities/Resto/ProductModifierGroup.cs src/Negosio.Infrastructure/Persistence/Configurations/ModifierGroupConfiguration.cs src/Negosio.Infrastructure/Persistence/Configurations/ProductModifierGroupConfiguration.cs tests/Negosio.UnitTests/Resto/ModifierGroupTests.cs
git commit -m "feat(resto): add ModifierGroup, ModifierOption, ProductModifierGroup entities"
```

---

## Task 5: `RestoOrder`

**Files:**
- Create: `src/Negosio.Domain/Entities/Resto/RestoOrder.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Resto/RestoOrderTests.cs`

**Interfaces:**
- Consumes: `RestoServiceType`, `RestoOrderStatus`, `RestoOrderRoundStatus` (Task 1). `RestoTable` (Task 3, referenced by id only).
- Produces: `RestoOrder.OpenPayAsYouOrder(tenantId, branchId, registerSessionId, openedByUserId, displayLabel?) -> RestoOrder`; `RestoOrder.OpenBillOut(tenantId, branchId, registerSessionId, openedByUserId, tableId, displayLabel?) -> RestoOrder`; `OpenNextRound() -> RestoOrderRound`; `Settle(saleId, nowUtc)`; `Cancel(cancelledByUserId, reason, nowUtc)`; properties `ServiceType, TableId, DisplayLabel, Status, RegisterSessionId, OpenedByUserId, OpenedAtUtc, SettledAtUtc, SaleId, CancelledAtUtc, CancelledByUserId, CancelReason, Rounds`. Task 6 (`RestoOrderRound`) is constructed only through `OpenNextRound()`. Task 11 (migration) needs the filtered-unique-index shape this task's configuration defines.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Negosio.UnitTests/Resto/RestoOrderTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();
    private static readonly Guid RegisterSessionId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TableId = Guid.NewGuid();

    [Fact]
    public void OpenPayAsYouOrder_has_no_table_and_starts_open()
    {
        var order = RestoOrder.OpenPayAsYouOrder(TenantId, BranchId, RegisterSessionId, UserId, "Counter 3");

        order.ServiceType.Should().Be(RestoServiceType.PayAsYouOrder);
        order.TableId.Should().BeNull();
        order.DisplayLabel.Should().Be("Counter 3");
        order.Status.Should().Be(RestoOrderStatus.Open);
        order.Rounds.Should().BeEmpty();
    }

    [Fact]
    public void OpenBillOut_requires_a_table()
    {
        var act = () => RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, Guid.Empty, null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OpenBillOut_with_a_table_starts_open()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null);

        order.ServiceType.Should().Be(RestoServiceType.BillOut);
        order.TableId.Should().Be(TableId);
        order.Status.Should().Be(RestoOrderStatus.Open);
    }

    [Fact]
    public void OpenNextRound_numbers_rounds_sequentially_starting_at_one()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null);

        var round1 = order.OpenNextRound();
        var round2 = order.OpenNextRound();

        round1.RoundNumber.Should().Be(1);
        round2.RoundNumber.Should().Be(2);
        order.Rounds.Should().HaveCount(2);
    }

    [Fact]
    public void Settle_requires_the_order_to_be_open()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null);
        order.Settle(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => order.Settle(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Settle_sets_SaleId_and_flips_status()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null);
        var saleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        order.Settle(saleId, now);

        order.Status.Should().Be(RestoOrderStatus.Settled);
        order.SaleId.Should().Be(saleId);
        order.SettledAtUtc.Should().Be(now);
    }

    [Fact]
    public void Cancel_succeeds_when_no_round_has_been_released()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null);
        order.OpenNextRound();
        var now = DateTime.UtcNow;

        order.Cancel(UserId, "Customer left", now);

        order.Status.Should().Be(RestoOrderStatus.Cancelled);
        order.CancelledByUserId.Should().Be(UserId);
        order.CancelReason.Should().Be("Customer left");
        order.CancelledAtUtc.Should().Be(now);
    }

    [Fact]
    public void Cancel_is_rejected_once_any_round_has_been_released()
    {
        var order = RestoOrder.OpenBillOut(TenantId, BranchId, RegisterSessionId, UserId, TableId, null);
        var round = order.OpenNextRound();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => order.Cancel(UserId, "Too late", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderTests"`
Expected: build error (also references `RestoOrderRound.Release`, built in Task 6 — this test file will not fully compile until Task 6 lands; write `RestoOrder` first in this task and leave the last test's `round.Release(...)` call commented out with a `// TODO(Task 6): uncomment once RestoOrderRound.Release exists` marker if Task 6 is not yet done, then uncomment it as part of Task 6's own step 1).

- [ ] **Step 3: Implement `RestoOrder`**

```csharp
// src/Negosio.Domain/Entities/Resto/RestoOrder.cs
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>The pre-Sale aggregate shared by both RestoPOS service types (design spec Section 3/4).
/// Owns one or more <see cref="RestoOrderRound"/>s. The only difference between Pay-as-you-order and
/// Bill-Out is a workflow constraint enforced by the application service that calls this aggregate
/// (PAYO allows exactly one round and forces settlement before release; Bill-Out allows releasing
/// while unpaid and allows more rounds) — this class does not itself enforce that distinction beyond
/// the <see cref="TableId"/> requirement below, since "may this round release yet" depends on
/// <see cref="Sale"/> state this aggregate does not hold.
///
/// <see cref="RowVersion"/> is a client-sent-expected-version conflict signal (design spec Section
/// 6.3) — checked by the application service after it acquires the pessimistic
/// <c>WITH (UPDLOCK, HOLDLOCK)</c> lock on this row, not a substitute for that lock.</summary>
public class RestoOrder : Entity
{
    private readonly List<RestoOrderRound> _rounds = new();

    private RestoOrder()
    {
    }

    private RestoOrder(
        Guid tenantId,
        Guid branchId,
        Guid registerSessionId,
        RestoServiceType serviceType,
        Guid? tableId,
        string? displayLabel,
        Guid openedByUserId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        RegisterSessionId = registerSessionId;
        ServiceType = serviceType;
        TableId = tableId;
        DisplayLabel = string.IsNullOrWhiteSpace(displayLabel) ? null : displayLabel.Trim();
        OpenedByUserId = openedByUserId;
        OpenedAtUtc = DateTime.UtcNow;
        Status = RestoOrderStatus.Open;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid RegisterSessionId { get; private set; }

    public RestoServiceType ServiceType { get; private set; }

    /// <summary>Required for <see cref="RestoServiceType.BillOut"/>, always null for
    /// <see cref="RestoServiceType.PayAsYouOrder"/> — enforced once, at creation, since
    /// <see cref="ServiceType"/> never changes after that.</summary>
    public Guid? TableId { get; private set; }

    public string? DisplayLabel { get; private set; }

    public RestoOrderStatus Status { get; private set; }

    public Guid OpenedByUserId { get; private set; }

    public DateTime OpenedAtUtc { get; private set; }

    public DateTime? SettledAtUtc { get; private set; }

    public Guid? SaleId { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>SQL Server `rowversion` — EF-managed. See design spec Section 6.3 for how the
    /// application service is required to use this.</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public IReadOnlyCollection<RestoOrderRound> Rounds => _rounds.AsReadOnly();

    public static RestoOrder OpenPayAsYouOrder(
        Guid tenantId, Guid branchId, Guid registerSessionId, Guid openedByUserId, string? displayLabel) =>
        new(tenantId, branchId, registerSessionId, RestoServiceType.PayAsYouOrder, tableId: null, displayLabel, openedByUserId);

    public static RestoOrder OpenBillOut(
        Guid tenantId, Guid branchId, Guid registerSessionId, Guid openedByUserId, Guid tableId, string? displayLabel)
    {
        if (tableId == Guid.Empty)
        {
            throw new ArgumentException("A Bill-Out order requires a table.", nameof(tableId));
        }

        return new RestoOrder(tenantId, branchId, registerSessionId, RestoServiceType.BillOut, tableId, displayLabel, openedByUserId);
    }

    public RestoOrderRound OpenNextRound()
    {
        var round = RestoOrderRound.Create(TenantId, Id, _rounds.Count + 1);
        _rounds.Add(round);
        return round;
    }

    /// <summary>Called by the settlement use case once the resulting <see cref="Sale"/> has already
    /// been created in the same transaction. <paramref name="nowUtc"/> is caller-supplied so the
    /// timestamp matches whatever the settlement transaction used elsewhere, exactly like
    /// <see cref="Sale.Void"/>'s own convention.</summary>
    public void Settle(Guid saleId, DateTime nowUtc)
    {
        if (Status != RestoOrderStatus.Open)
        {
            throw new InvalidOperationException("Only an open order can be settled.");
        }

        Status = RestoOrderStatus.Settled;
        SaleId = saleId;
        SettledAtUtc = nowUtc;
        Touch();
    }

    /// <summary>Only valid while every round is still <see cref="RestoOrderRoundStatus.Draft"/> or
    /// <see cref="RestoOrderRoundStatus.Voided"/> — the moment anything has been
    /// <see cref="RestoOrderRoundStatus.Released"/>, food has genuinely been sent to the kitchen and
    /// this is no longer a "nothing happened" cancellation (design spec Section 10, Unresolved
    /// Decision #2). The application service must route that case through Settlement instead.</summary>
    public void Cancel(Guid cancelledByUserId, string reason, DateTime nowUtc)
    {
        if (Status != RestoOrderStatus.Open)
        {
            throw new InvalidOperationException("Only an open order can be cancelled.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        }

        if (_rounds.Any(r => r.Status == RestoOrderRoundStatus.Released))
        {
            throw new InvalidOperationException(
                "This order has a released round and can no longer be cancelled outright — settle it instead.");
        }

        Status = RestoOrderStatus.Cancelled;
        CancelledByUserId = cancelledByUserId;
        CancelReason = reason.Trim();
        CancelledAtUtc = nowUtc;
        Touch();
    }
}
```

- [ ] **Step 4: Run to confirm it passes** (once Task 6 also lands — see the note in Step 2; if doing Tasks 5 and 6 in strict order, this step's `dotnet test` run will still fail to compile on the `round.Release(...)` line until Task 6 is done, which is expected and not a defect in this task)

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderTests"`
Expected: compiles and 7/7 pass once Task 6's `RestoOrderRound.Release` exists.

- [ ] **Step 5: Write the EF configuration**

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderConfiguration : IEntityTypeConfiguration<RestoOrder>
{
    public void Configure(EntityTypeBuilder<RestoOrder> builder)
    {
        builder.ToTable("RestoOrders");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.BranchId).IsRequired();
        builder.Property(o => o.RegisterSessionId).IsRequired();
        builder.Property(o => o.ServiceType).IsRequired().HasConversion<int>();
        builder.Property(o => o.DisplayLabel).HasMaxLength(100);
        builder.Property(o => o.Status).IsRequired().HasConversion<int>();
        builder.Property(o => o.OpenedByUserId).IsRequired();
        builder.Property(o => o.OpenedAtUtc).IsRequired();
        builder.Property(o => o.CancelReason).HasMaxLength(500);
        builder.Property(o => o.RowVersion).IsRowVersion();
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();

        builder.HasOne<RestoTable>()
            .WithMany()
            .HasForeignKey(o => o.TableId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RegisterSession>()
            .WithMany()
            .HasForeignKey(o => o.RegisterSessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(o => o.Rounds)
            .WithOne()
            .HasForeignKey(r => r.RestoOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Rounds).UsePropertyAccessMode(PropertyAccessMode.Field);

        // At most one Open RestoOrder per table — mirrors IX_RegisterSessions_RegisterId_Open exactly.
        // This is the constraint that actually prevents two concurrent visits on the same table.
        builder.HasIndex(o => o.TableId)
            .IsUnique()
            .HasFilter($"[Status] = {(int)RestoOrderStatus.Open} AND [TableId] IS NOT NULL")
            .HasDatabaseName("IX_RestoOrders_TableId_Open");

        builder.HasIndex(o => new { o.TenantId, o.BranchId, o.Status })
            .HasDatabaseName("IX_RestoOrders_TenantId_BranchId_Status");
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors (will only succeed once Task 6's `RestoOrderRound` exists, since this configuration references it — acceptable if Tasks 5 and 6 are done back-to-back by the same implementer, or note the temporary build break to the task reviewer if done as strictly separate dispatches).

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Domain/Entities/Resto/RestoOrder.cs src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderConfiguration.cs tests/Negosio.UnitTests/Resto/RestoOrderTests.cs
git commit -m "feat(resto): add RestoOrder aggregate"
```

---

## Task 6: `RestoOrderRound`

**Files:**
- Create: `src/Negosio.Domain/Entities/Resto/RestoOrderRound.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderRoundConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Resto/RestoOrderRoundTests.cs`
- Modify: `tests/Negosio.UnitTests/Resto/RestoOrderTests.cs` — uncomment/complete the `Cancel_is_rejected_once_any_round_has_been_released` test's use of `round.Release(...)` if Task 5 left it stubbed.

**Interfaces:**
- Consumes: `RestoOrderRoundStatus` (Task 1). Constructed only via `RestoOrder.OpenNextRound()` (Task 5) — this task's `Create` factory is `internal`, matching `ModifierOption`'s constructor visibility in Task 4.
- Produces: `RestoOrderRound.Create(tenantId, restoOrderId, roundNumber) -> RestoOrderRound` (internal); `AddItem(...) -> RestoOrderItem`; `Release(releasedByUserId, nowUtc)`; `VoidWhole()` (no parameters — for a round scrapped before ever being sent); properties `RestoOrderId, RoundNumber, Status, ReleasedAtUtc, ReleasedByUserId, Items`. Task 7 (`RestoOrderItem`) is constructed only through `AddItem`.

- [ ] **Step 1: Write the failing test**

**Note on visibility:** `RestoOrderRound.Create` (below) is `internal` — this codebase has no `InternalsVisibleTo` anywhere (confirmed by search), so a test in `Negosio.UnitTests` cannot call it directly. Every test below constructs its round the only way external code legitimately can: through the public `RestoOrder.OpenBillOut(...).OpenNextRound()` chain from Task 5.

```csharp
// tests/Negosio.UnitTests/Resto/RestoOrderRoundTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderRoundTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid StationId = Guid.NewGuid();
    private static readonly Guid ProductVariantId = Guid.NewGuid();

    private static RestoOrder NewOrder() =>
        RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null);

    [Fact]
    public void OpenNextRound_starts_in_Draft_with_no_items()
    {
        var order = NewOrder();

        var round = order.OpenNextRound();

        round.RestoOrderId.Should().Be(order.Id);
        round.RoundNumber.Should().Be(1);
        round.Status.Should().Be(RestoOrderRoundStatus.Draft);
        round.Items.Should().BeEmpty();
    }

    [Fact]
    public void Release_flips_status_and_puts_every_item_into_Pending()
    {
        var round = NewOrder().OpenNextRound();
        var item = round.AddItem(
            ProductVariantId, "Burger", null, StationId, "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: null);
        var now = DateTime.UtcNow;

        round.Release(UserId, now);

        round.Status.Should().Be(RestoOrderRoundStatus.Released);
        round.ReleasedByUserId.Should().Be(UserId);
        round.ReleasedAtUtc.Should().Be(now);
        item.KitchenStatus.Should().Be(RestoKitchenStatus.Pending);
    }

    [Fact]
    public void Release_is_idempotent()
    {
        var round = NewOrder().OpenNextRound();
        var firstUser = UserId;
        var firstTime = DateTime.UtcNow;
        round.Release(firstUser, firstTime);

        round.Release(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));

        round.ReleasedByUserId.Should().Be(firstUser, "a retried release must not overwrite who actually released it");
        round.ReleasedAtUtc.Should().Be(firstTime);
    }

    [Fact]
    public void AddItem_is_rejected_once_the_round_has_been_released()
    {
        var round = NewOrder().OpenNextRound();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => round.AddItem(
            ProductVariantId, "Burger", null, StationId, "Kitchen",
            150m, 12m, 150m, 0m, 18m, 150m, 1m, null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void VoidWhole_flips_status_from_Draft()
    {
        var round = NewOrder().OpenNextRound();

        round.VoidWhole();

        round.Status.Should().Be(RestoOrderRoundStatus.Voided);
    }

    [Fact]
    public void VoidWhole_is_rejected_once_the_round_has_been_released()
    {
        var round = NewOrder().OpenNextRound();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => round.VoidWhole();

        act.Should().Throw<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderRoundTests"`
Expected: build error.

- [ ] **Step 3: Implement `RestoOrderRound`**

```csharp
// src/Negosio.Domain/Entities/Resto/RestoOrderRound.cs
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>One "send to kitchen" batch within a <see cref="RestoOrder"/>. Deliberately thin —
/// <see cref="Status"/> only tracks whether this round has been sent at all; the real per-station
/// kitchen workflow lives on each <see cref="RestoOrderItem.KitchenStatus"/> (design spec Section 2,
/// "On your station question").</summary>
public class RestoOrderRound : Entity
{
    private readonly List<RestoOrderItem> _items = new();

    private RestoOrderRound()
    {
    }

    private RestoOrderRound(Guid tenantId, Guid restoOrderId, int roundNumber)
    {
        TenantId = tenantId;
        RestoOrderId = restoOrderId;
        RoundNumber = roundNumber;
        Status = RestoOrderRoundStatus.Draft;
    }

    public Guid TenantId { get; private set; }

    public Guid RestoOrderId { get; private set; }

    public int RoundNumber { get; private set; }

    public RestoOrderRoundStatus Status { get; private set; }

    public DateTime? ReleasedAtUtc { get; private set; }

    public Guid? ReleasedByUserId { get; private set; }

    public IReadOnlyCollection<RestoOrderItem> Items => _items.AsReadOnly();

    internal static RestoOrderRound Create(Guid tenantId, Guid restoOrderId, int roundNumber) =>
        new(tenantId, restoOrderId, roundNumber);

    public RestoOrderItem AddItem(
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        Guid stationId,
        string stationNameSnapshot,
        decimal unitPriceSnapshot,
        decimal taxRateSnapshot,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal quantity,
        string? kitchenNote)
    {
        if (Status != RestoOrderRoundStatus.Draft)
        {
            throw new InvalidOperationException("Items can only be added to a round that hasn't been released yet.");
        }

        var item = RestoOrderItem.Create(
            TenantId, Id, productVariantId, productNameSnapshot, variantNameSnapshot, stationId, stationNameSnapshot,
            unitPriceSnapshot, taxRateSnapshot, grossAmount, discountAmount, taxAmount, netAmount, quantity, kitchenNote);
        _items.Add(item);
        return item;
    }

    /// <summary>Idempotent by design (design spec Section 6.1) — calling this on an already-Released
    /// round is a no-op that preserves who originally released it, so a retried release call (the
    /// manual recovery button, or the background reconciliation worker) never overwrites the audit
    /// trail or double-fires a ticket.</summary>
    public void Release(Guid releasedByUserId, DateTime nowUtc)
    {
        if (Status == RestoOrderRoundStatus.Released)
        {
            return;
        }

        if (Status != RestoOrderRoundStatus.Draft)
        {
            throw new InvalidOperationException("Only a draft round can be released.");
        }

        Status = RestoOrderRoundStatus.Released;
        ReleasedByUserId = releasedByUserId;
        ReleasedAtUtc = nowUtc;
        foreach (var item in _items.Where(i => i.VoidedAtUtc is null))
        {
            item.EnterKitchenQueue();
        }

        Touch();
    }

    /// <summary>The whole round is scrapped before it was ever sent (e.g. a cashier mis-added a round
    /// and clears it). No per-item void events are needed since nothing was ever sent anywhere.</summary>
    public void VoidWhole()
    {
        if (Status != RestoOrderRoundStatus.Draft)
        {
            throw new InvalidOperationException("Only a draft round can be voided outright.");
        }

        Status = RestoOrderRoundStatus.Voided;
        Touch();
    }
}
```

- [ ] **Step 4: Run to confirm it passes**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderRoundTests"`
Expected: 6/6 passing (this will also require Task 7's `RestoOrderItem.Create`/`EnterKitchenQueue` to exist — do Tasks 6 and 7 back-to-back, or stub `RestoOrderItem` minimally first; the task boundary here is drawn at "round behavior," not "zero cross-file dependency," since a round without any item shape is untestable for its own core `AddItem`/`Release` behavior).

- [ ] **Step 5: Write the EF configuration**

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderRoundConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderRoundConfiguration : IEntityTypeConfiguration<RestoOrderRound>
{
    public void Configure(EntityTypeBuilder<RestoOrderRound> builder)
    {
        builder.ToTable("RestoOrderRounds");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.RestoOrderId).IsRequired();
        builder.Property(r => r.RoundNumber).IsRequired();
        builder.Property(r => r.Status).IsRequired().HasConversion<int>();
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        builder.HasMany(r => r.Items)
            .WithOne()
            .HasForeignKey(i => i.RestoOrderRoundId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => new { r.TenantId, r.RestoOrderId })
            .HasDatabaseName("IX_RestoOrderRounds_TenantId_RestoOrderId");
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors once Task 7 also exists.

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Domain/Entities/Resto/RestoOrderRound.cs src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderRoundConfiguration.cs tests/Negosio.UnitTests/Resto/RestoOrderRoundTests.cs tests/Negosio.UnitTests/Resto/RestoOrderTests.cs
git commit -m "feat(resto): add RestoOrderRound entity"
```

---

## Task 7: `RestoOrderItem`

**Files:**
- Create: `src/Negosio.Domain/Entities/Resto/RestoOrderItem.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderItemConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Resto/RestoOrderItemTests.cs`

**Interfaces:**
- Consumes: `RestoKitchenStatus` (Task 1). Constructed only via `RestoOrderRound.AddItem(...)` (Task 6) — this task's `Create` factory and `EnterKitchenQueue()` are `internal`.
- Produces: `RestoOrderItem.Create(...) -> RestoOrderItem` (internal, called by Task 6); `EnterKitchenQueue()` (internal, called by Task 6's `Release`); `Acknowledge(userId, nowUtc)`, `MarkReady(userId, nowUtc)`, `MarkServed(userId, nowUtc)` (each a guarded, sequential transition); `Void(voidedByUserId, reason, approvedByUserId, nowUtc)`; `AddModifier(...) -> RestoOrderItemModifier`. Task 8 (`RestoOrderItemModifier`) is constructed only through `AddModifier`.

- [ ] **Step 1: Write the failing test**

**Note on visibility:** `RestoOrderItem.Create` and `RestoOrderItem.EnterKitchenQueue` are both `internal` (same "no `InternalsVisibleTo`" reason as Task 6). Every test constructs its item via the public `RestoOrder.OpenBillOut(...).OpenNextRound().AddItem(...)` chain, and drives it into the kitchen queue via the public `RestoOrderRound.Release(...)` (Task 6) rather than calling `EnterKitchenQueue` directly.

```csharp
// tests/Negosio.UnitTests/Resto/RestoOrderItemTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderItemTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ProductVariantId = Guid.NewGuid();
    private static readonly Guid StationId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static (RestoOrderRound Round, RestoOrderItem Item) NewItem()
    {
        var order = RestoOrder.OpenBillOut(TenantId, Guid.NewGuid(), Guid.NewGuid(), UserId, Guid.NewGuid(), null);
        var round = order.OpenNextRound();
        var item = round.AddItem(
            ProductVariantId, "Burger", "Regular", StationId, "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: "No onions");
        return (round, item);
    }

    [Fact]
    public void AddItem_freezes_every_snapshot_field_and_has_no_KitchenStatus_yet()
    {
        var (_, item) = NewItem();

        item.ProductNameSnapshot.Should().Be("Burger");
        item.VariantNameSnapshot.Should().Be("Regular");
        item.StationId.Should().Be(StationId);
        item.StationNameSnapshot.Should().Be("Kitchen");
        item.UnitPriceSnapshot.Should().Be(150m);
        item.GrossAmount.Should().Be(150m);
        item.NetAmount.Should().Be(150m);
        item.KitchenNote.Should().Be("No onions");
        item.KitchenStatus.Should().BeNull();
        item.VoidedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Releasing_the_round_sets_KitchenStatus_to_Pending()
    {
        var (round, item) = NewItem();

        round.Release(UserId, DateTime.UtcNow);

        item.KitchenStatus.Should().Be(RestoKitchenStatus.Pending);
    }

    [Fact]
    public void Status_transitions_must_happen_in_order()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);

        var act = () => item.MarkReady(UserId, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>("an item cannot skip Acknowledged");
    }

    [Fact]
    public void Acknowledge_then_Ready_then_Served_records_each_actor_and_timestamp()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);
        var t1 = DateTime.UtcNow;
        var t2 = t1.AddMinutes(2);
        var t3 = t1.AddMinutes(5);

        item.Acknowledge(UserId, t1);
        item.MarkReady(UserId, t2);
        item.MarkServed(UserId, t3);

        item.KitchenStatus.Should().Be(RestoKitchenStatus.Served);
        item.AcknowledgedAtUtc.Should().Be(t1);
        item.ReadyAtUtc.Should().Be(t2);
        item.ServedAtUtc.Should().Be(t3);
        item.ServedByUserId.Should().Be(UserId);
    }

    [Fact]
    public void Void_is_allowed_at_any_KitchenStatus_and_never_deletes_the_item()
    {
        var (round, item) = NewItem();
        round.Release(UserId, DateTime.UtcNow);
        item.Acknowledge(UserId, DateTime.UtcNow);
        var now = DateTime.UtcNow;

        item.Void(UserId, "Kitchen made a mistake", approvedByUserId: null, now);

        item.VoidedAtUtc.Should().Be(now);
        item.VoidedByUserId.Should().Be(UserId);
        item.VoidReason.Should().Be("Kitchen made a mistake");
        item.KitchenStatus.Should().Be(RestoKitchenStatus.Acknowledged, "void never rewrites the kitchen-status history");
    }

    [Fact]
    public void Void_cannot_be_called_twice()
    {
        var (_, item) = NewItem();
        item.Void(UserId, "Mistake", null, DateTime.UtcNow);

        var act = () => item.Void(UserId, "Again", null, DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddModifier_appends_a_frozen_modifier_line()
    {
        var (_, item) = NewItem();

        var modifier = item.AddModifier("Add-ons", "Extra cheese", 20m);

        item.Modifiers.Should().ContainSingle().Which.Should().BeSameAs(modifier);
        modifier.ModifierGroupNameSnapshot.Should().Be("Add-ons");
        modifier.ModifierOptionNameSnapshot.Should().Be("Extra cheese");
        modifier.PriceDeltaSnapshot.Should().Be(20m);
    }
}
```

- [ ] **Step 2: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderItemTests"`
Expected: build error.

- [ ] **Step 3: Implement `RestoOrderItem`**

```csharp
// src/Negosio.Domain/Entities/Resto/RestoOrderItem.cs
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>One ordered line within a <see cref="RestoOrderRound"/>. Every pricing/identity field is
/// a snapshot frozen at add-time via the existing <c>SaleLineCalculator</c> (see the design spec
/// Section 2.1's verified rounding invariant) and never re-derived — settlement copies
/// <see cref="GrossAmount"/>/<see cref="DiscountAmount"/>/<see cref="TaxAmount"/>/<see cref="NetAmount"/>
/// straight into the resulting <c>SaleItem</c>. A void never deletes the row or rewrites
/// <see cref="KitchenStatus"/>'s history — it stays visible in its round for the audit trail, and
/// settlement simply skips voided items when building the Sale (design spec Section 5).</summary>
public class RestoOrderItem : Entity
{
    private readonly List<RestoOrderItemModifier> _modifiers = new();

    private RestoOrderItem()
    {
        ProductNameSnapshot = string.Empty;
        StationNameSnapshot = string.Empty;
    }

    private RestoOrderItem(
        Guid tenantId,
        Guid restoOrderRoundId,
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        Guid stationId,
        string stationNameSnapshot,
        decimal unitPriceSnapshot,
        decimal taxRateSnapshot,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal quantity,
        string? kitchenNote)
    {
        TenantId = tenantId;
        RestoOrderRoundId = restoOrderRoundId;
        ProductVariantId = productVariantId;
        ProductNameSnapshot = productNameSnapshot;
        VariantNameSnapshot = variantNameSnapshot;
        StationId = stationId;
        StationNameSnapshot = stationNameSnapshot;
        UnitPriceSnapshot = unitPriceSnapshot;
        TaxRateSnapshot = taxRateSnapshot;
        GrossAmount = grossAmount;
        DiscountAmount = discountAmount;
        TaxAmount = taxAmount;
        NetAmount = netAmount;
        Quantity = quantity;
        KitchenNote = string.IsNullOrWhiteSpace(kitchenNote) ? null : kitchenNote.Trim();
    }

    public Guid TenantId { get; private set; }

    public Guid RestoOrderRoundId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string? VariantNameSnapshot { get; private set; }

    /// <summary>Retained even if the station is later renamed or disabled — see
    /// <see cref="RestoStation"/>'s doc comment.</summary>
    public Guid StationId { get; private set; }

    public string StationNameSnapshot { get; private set; }

    public decimal UnitPriceSnapshot { get; private set; }

    public decimal TaxRateSnapshot { get; private set; }

    public decimal GrossAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public decimal Quantity { get; private set; }

    public string? KitchenNote { get; private set; }

    public RestoKitchenStatus? KitchenStatus { get; private set; }

    public DateTime? AcknowledgedAtUtc { get; private set; }

    public Guid? AcknowledgedByUserId { get; private set; }

    public DateTime? ReadyAtUtc { get; private set; }

    public Guid? ReadyByUserId { get; private set; }

    public DateTime? ServedAtUtc { get; private set; }

    public Guid? ServedByUserId { get; private set; }

    public DateTime? VoidedAtUtc { get; private set; }

    public Guid? VoidedByUserId { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public string? VoidReason { get; private set; }

    public IReadOnlyCollection<RestoOrderItemModifier> Modifiers => _modifiers.AsReadOnly();

    internal static RestoOrderItem Create(
        Guid tenantId,
        Guid restoOrderRoundId,
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        Guid stationId,
        string stationNameSnapshot,
        decimal unitPriceSnapshot,
        decimal taxRateSnapshot,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal quantity,
        string? kitchenNote)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        return new RestoOrderItem(
            tenantId, restoOrderRoundId, productVariantId, productNameSnapshot, variantNameSnapshot,
            stationId, stationNameSnapshot, unitPriceSnapshot, taxRateSnapshot, grossAmount, discountAmount,
            taxAmount, netAmount, quantity, kitchenNote);
    }

    /// <summary>Called by <see cref="RestoOrderRound.Release"/> for every non-voided item in the round.</summary>
    internal void EnterKitchenQueue()
    {
        KitchenStatus = RestoKitchenStatus.Pending;
    }

    public RestoOrderItemModifier AddModifier(string modifierGroupNameSnapshot, string modifierOptionNameSnapshot, decimal priceDeltaSnapshot)
    {
        var modifier = RestoOrderItemModifier.Create(TenantId, Id, modifierGroupNameSnapshot, modifierOptionNameSnapshot, priceDeltaSnapshot);
        _modifiers.Add(modifier);
        return modifier;
    }

    public void Acknowledge(Guid userId, DateTime nowUtc)
    {
        if (KitchenStatus != RestoKitchenStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending item can be acknowledged.");
        }

        KitchenStatus = RestoKitchenStatus.Acknowledged;
        AcknowledgedAtUtc = nowUtc;
        AcknowledgedByUserId = userId;
        Touch();
    }

    public void MarkReady(Guid userId, DateTime nowUtc)
    {
        if (KitchenStatus != RestoKitchenStatus.Acknowledged)
        {
            throw new InvalidOperationException("Only an acknowledged item can be marked ready.");
        }

        KitchenStatus = RestoKitchenStatus.Ready;
        ReadyAtUtc = nowUtc;
        ReadyByUserId = userId;
        Touch();
    }

    public void MarkServed(Guid userId, DateTime nowUtc)
    {
        if (KitchenStatus != RestoKitchenStatus.Ready)
        {
            throw new InvalidOperationException("Only a ready item can be marked served.");
        }

        KitchenStatus = RestoKitchenStatus.Served;
        ServedAtUtc = nowUtc;
        ServedByUserId = userId;
        Touch();
    }

    /// <summary>Allowed at any <see cref="KitchenStatus"/>, including null (never released) — the only
    /// gate is that the caller must not call this once the parent <c>RestoOrder</c> has been settled,
    /// which this entity cannot check itself (it doesn't hold a reference to its order's status) and
    /// is therefore the calling application service's responsibility, not this method's.</summary>
    public void Void(Guid voidedByUserId, string reason, Guid? approvedByUserId, DateTime nowUtc)
    {
        if (VoidedAtUtc is not null)
        {
            throw new InvalidOperationException("This item has already been voided.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A void reason is required.", nameof(reason));
        }

        VoidedAtUtc = nowUtc;
        VoidedByUserId = voidedByUserId;
        ApprovedByUserId = approvedByUserId;
        VoidReason = reason.Trim();
        Touch();
    }
}
```

- [ ] **Step 4: Run to confirm it passes**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderItemTests"`
Expected: 7/7 passing.

- [ ] **Step 5: Write the EF configuration**

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderItemConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderItemConfiguration : IEntityTypeConfiguration<RestoOrderItem>
{
    public void Configure(EntityTypeBuilder<RestoOrderItem> builder)
    {
        builder.ToTable("RestoOrderItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.RestoOrderRoundId).IsRequired();
        builder.Property(i => i.ProductVariantId).IsRequired();
        builder.Property(i => i.ProductNameSnapshot).IsRequired().HasMaxLength(150);
        builder.Property(i => i.VariantNameSnapshot).HasMaxLength(150);
        builder.Property(i => i.StationId).IsRequired();
        builder.Property(i => i.StationNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(i => i.UnitPriceSnapshot).HasPrecision(18, 2);
        builder.Property(i => i.TaxRateSnapshot).HasPrecision(9, 4);
        builder.Property(i => i.GrossAmount).HasPrecision(18, 2);
        builder.Property(i => i.DiscountAmount).HasPrecision(18, 2);
        builder.Property(i => i.TaxAmount).HasPrecision(18, 2);
        builder.Property(i => i.NetAmount).HasPrecision(18, 2);
        builder.Property(i => i.Quantity).HasPrecision(18, 3);
        builder.Property(i => i.KitchenNote).HasMaxLength(500);
        builder.Property(i => i.KitchenStatus).HasConversion<int?>();
        builder.Property(i => i.VoidReason).HasMaxLength(500);
        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc).IsRequired();

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RestoStation>()
            .WithMany()
            .HasForeignKey(i => i.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(i => i.Modifiers)
            .WithOne()
            .HasForeignKey(m => m.RestoOrderItemId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Modifiers).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(i => new { i.TenantId, i.RestoOrderRoundId })
            .HasDatabaseName("IX_RestoOrderItems_TenantId_RestoOrderRoundId");
        builder.HasIndex(i => new { i.TenantId, i.StationId, i.KitchenStatus })
            .HasDatabaseName("IX_RestoOrderItems_TenantId_StationId_KitchenStatus");
    }
}
```

`ProductVariant` needs no separate `using` beyond the one already in this file — see Task 4's note on this codebase's flat-namespace convention.

- [ ] **Step 6: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors once Task 8 also exists.

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Domain/Entities/Resto/RestoOrderItem.cs src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderItemConfiguration.cs tests/Negosio.UnitTests/Resto/RestoOrderItemTests.cs
git commit -m "feat(resto): add RestoOrderItem entity"
```

---

## Task 8: `RestoOrderItemModifier`

**Files:**
- Create: `src/Negosio.Domain/Entities/Resto/RestoOrderItemModifier.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderItemModifierConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Resto/RestoOrderItemModifierTests.cs`

**Interfaces:**
- Consumes: nothing new — constructed only via `RestoOrderItem.AddModifier(...)` (Task 7), so `Create` is `internal`.
- Produces: properties `RestoOrderItemId, ModifierGroupNameSnapshot, ModifierOptionNameSnapshot, PriceDeltaSnapshot`. No later task in this plan depends on this beyond Task 7 already using it.

- [ ] **Step 1: Write the failing test**

**Note on visibility:** `RestoOrderItemModifier.Create` is `internal` (same reason as Tasks 6-7). The test constructs a modifier the only way external code can: via the public `RestoOrderItem.AddModifier(...)` (Task 7), itself reached through the full `RestoOrder → OpenNextRound → AddItem` chain. This overlaps with Task 7's own `AddModifier_appends_a_frozen_modifier_line` test — that overlap is intentional: each task's own commit needs its own passing coverage for the file it adds, even where a neighboring task's test already exercises the same public method incidentally.

```csharp
// tests/Negosio.UnitTests/Resto/RestoOrderItemModifierTests.cs
using FluentAssertions;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.UnitTests.Resto;

public class RestoOrderItemModifierTests
{
    [Fact]
    public void AddModifier_on_an_item_freezes_the_given_snapshot_values()
    {
        var tenantId = Guid.NewGuid();
        var order = RestoOrder.OpenBillOut(tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        var round = order.OpenNextRound();
        var item = round.AddItem(
            Guid.NewGuid(), "Burger", null, Guid.NewGuid(), "Kitchen",
            unitPriceSnapshot: 150m, taxRateSnapshot: 12m,
            grossAmount: 150m, discountAmount: 0m, taxAmount: 18m, netAmount: 150m,
            quantity: 1m, kitchenNote: null);

        var modifier = item.AddModifier("Add-ons", "Extra cheese", 20m);

        modifier.TenantId.Should().Be(tenantId);
        modifier.RestoOrderItemId.Should().Be(item.Id);
        modifier.ModifierGroupNameSnapshot.Should().Be("Add-ons");
        modifier.ModifierOptionNameSnapshot.Should().Be("Extra cheese");
        modifier.PriceDeltaSnapshot.Should().Be(20m);
    }
}
```

- [ ] **Step 2: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderItemModifierTests"`
Expected: build error.

- [ ] **Step 3: Implement `RestoOrderItemModifier`**

```csharp
// src/Negosio.Domain/Entities/Resto/RestoOrderItemModifier.cs
using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>A frozen record of one modifier chosen for a <see cref="RestoOrderItem"/> — both the
/// group and option names, and the price delta, are snapshotted at add-time so a later catalog edit
/// to the live <see cref="ModifierGroup"/>/<see cref="ModifierOption"/> can never change an open bill
/// or a historical order (same discipline as every other snapshot field in this module).</summary>
public class RestoOrderItemModifier : Entity
{
    private RestoOrderItemModifier()
    {
        ModifierGroupNameSnapshot = string.Empty;
        ModifierOptionNameSnapshot = string.Empty;
    }

    private RestoOrderItemModifier(
        Guid tenantId, Guid restoOrderItemId, string modifierGroupNameSnapshot, string modifierOptionNameSnapshot, decimal priceDeltaSnapshot)
    {
        TenantId = tenantId;
        RestoOrderItemId = restoOrderItemId;
        ModifierGroupNameSnapshot = modifierGroupNameSnapshot;
        ModifierOptionNameSnapshot = modifierOptionNameSnapshot;
        PriceDeltaSnapshot = priceDeltaSnapshot;
    }

    public Guid TenantId { get; private set; }

    public Guid RestoOrderItemId { get; private set; }

    public string ModifierGroupNameSnapshot { get; private set; }

    public string ModifierOptionNameSnapshot { get; private set; }

    public decimal PriceDeltaSnapshot { get; private set; }

    internal static RestoOrderItemModifier Create(
        Guid tenantId, Guid restoOrderItemId, string modifierGroupNameSnapshot, string modifierOptionNameSnapshot, decimal priceDeltaSnapshot) =>
        new(tenantId, restoOrderItemId, modifierGroupNameSnapshot, modifierOptionNameSnapshot, priceDeltaSnapshot);
}
```

- [ ] **Step 4: Run to confirm it passes**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~RestoOrderItemModifierTests"`
Expected: 1/1 passing.

- [ ] **Step 5: Write the EF configuration**

```csharp
// src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderItemModifierConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderItemModifierConfiguration : IEntityTypeConfiguration<RestoOrderItemModifier>
{
    public void Configure(EntityTypeBuilder<RestoOrderItemModifier> builder)
    {
        builder.ToTable("RestoOrderItemModifiers");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.RestoOrderItemId).IsRequired();
        builder.Property(m => m.ModifierGroupNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(m => m.ModifierOptionNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(m => m.PriceDeltaSnapshot).HasPrecision(18, 2);
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc).IsRequired();

        builder.HasIndex(m => new { m.TenantId, m.RestoOrderItemId })
            .HasDatabaseName("IX_RestoOrderItemModifiers_TenantId_RestoOrderItemId");
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Domain/Entities/Resto/RestoOrderItemModifier.cs src/Negosio.Infrastructure/Persistence/Configurations/RestoOrderItemModifierConfiguration.cs tests/Negosio.UnitTests/Resto/RestoOrderItemModifierTests.cs
git commit -m "feat(resto): add RestoOrderItemModifier entity"
```

---

## Task 9: `Sale.Origin`

**Files:**
- Modify: `src/Negosio.Domain/Entities/Sales/Sale.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Sales/SaleTests.cs` (create if it doesn't already exist; otherwise add to it — check first)

**Interfaces:**
- Consumes: `SaleOrigin` (Task 1).
- Produces: `Sale.Origin` (public getter, defaults to `SaleOrigin.Retail`); `Sale.Begin(...)` gains one new optional trailing parameter `SaleOrigin origin = SaleOrigin.Retail` so every existing call site (`CheckoutService.cs` and any test) keeps compiling unchanged. This is the one field the M2 plan's Return-rejection check and the future Resto settlement service both depend on.

- [ ] **Step 1: Check whether `tests/Negosio.UnitTests/Sales/SaleTests.cs` already exists**

Run: `find tests/Negosio.UnitTests -iname "SaleTests.cs"` (or the Windows-equivalent file search). If it exists, read it fully first and add the new test into it, matching its existing style, rather than creating a duplicate file.

- [ ] **Step 2: Write the failing test**

```csharp
// New or added-to tests/Negosio.UnitTests/Sales/SaleTests.cs
[Fact]
public void Begin_defaults_Origin_to_Retail()
{
    var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());

    sale.Origin.Should().Be(SaleOrigin.Retail);
}

[Fact]
public void Begin_accepts_an_explicit_Resto_origin()
{
    var sale = Sale.Begin(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid(),
        origin: SaleOrigin.Resto);

    sale.Origin.Should().Be(SaleOrigin.Resto);
}
```

(Add `using Negosio.Domain.Enums;` at the top of the test file if not already present. Match whatever test class name/namespace the existing file already uses if one is found in Step 1 — do not assume a fresh file is correct without checking.)

- [ ] **Step 3: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~SaleTests"`
Expected: build error — `Sale.Origin` and the `origin` parameter don't exist yet.

- [ ] **Step 4: Add `Origin` to `Sale`**

In `src/Negosio.Domain/Entities/Sales/Sale.cs`, add `using Negosio.Domain.Enums;` if not already imported (it already is, per the file's existing `using Negosio.Domain.Enums;` at the top), then:

Add the property (near the other simple properties, e.g. right after `Status`):
```csharp
public SaleOrigin Origin { get; private set; }
```

Modify the private full constructor to accept and set it:
```csharp
private Sale(
    Guid tenantId,
    Guid branchId,
    Guid registerSessionId,
    string saleNumber,
    Guid clientRequestId,
    Guid createdByUserId,
    SaleOrigin origin)
{
    TenantId = tenantId;
    BranchId = branchId;
    RegisterSessionId = registerSessionId;
    SaleNumber = saleNumber;
    ClientRequestId = clientRequestId;
    CreatedByUserId = createdByUserId;
    Status = SaleStatus.Completed;
    Origin = origin;
}
```

Modify `Begin` to add the optional trailing parameter, defaulting to `Retail` so every existing call site keeps compiling unchanged:
```csharp
public static Sale Begin(
    Guid tenantId,
    Guid branchId,
    Guid registerSessionId,
    string saleNumber,
    Guid clientRequestId,
    Guid createdByUserId,
    SaleOrigin origin = SaleOrigin.Retail) =>
    new(tenantId, branchId, registerSessionId, saleNumber, clientRequestId, createdByUserId, origin);
```

- [ ] **Step 5: Run to confirm the new tests pass and nothing else broke**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~SaleTests"`
Expected: passing, including any pre-existing tests in that file.

Run: `dotnet build Negosio.sln`
Expected: 0 errors — confirms every existing `Sale.Begin(...)` call site (`CheckoutService.cs` and any other caller) still compiles with the new optional parameter.

- [ ] **Step 6: Add `Origin` to `SaleConfiguration`**

In `src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs`, add one line near the other `Property(...)` calls:
```csharp
builder.Property(s => s.Origin).IsRequired().HasConversion<int>().HasDefaultValue(SaleOrigin.Retail);
```
Add `using Negosio.Domain.Enums;` to this file's usings if not already present.

- [ ] **Step 7: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Negosio.Domain/Entities/Sales/Sale.cs src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs tests/Negosio.UnitTests/Sales/SaleTests.cs
git commit -m "feat(resto): add Sale.Origin discriminator"
```

---

## Task 10: `Branch` service-type flags

**Files:**
- Modify: `src/Negosio.Domain/Entities/Branch.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/BranchConfiguration.cs`
- Test: `tests/Negosio.UnitTests/BranchTests.cs` (create if it doesn't already exist; check first, matching Task 9's approach)

**Interfaces:**
- Produces: `Branch.SupportsPayAsYouOrder`, `Branch.SupportsBillOut` (both `bool`, default `false`); `Branch.ConfigureRestoServiceTypes(bool supportsPayAsYouOrder, bool supportsBillOut)`. Enforced at order-creation time by a future M2 service, not by `Branch` itself.

- [ ] **Step 1: Check for an existing `BranchTests.cs`**

Run a file search the same way as Task 9's Step 1. Read it fully if found.

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public void New_branches_default_to_no_Resto_service_types_enabled()
{
    var branch = Branch.Create(Guid.NewGuid(), "Main", "MAIN", "123 St", null, "City", "Province", null, null);

    branch.SupportsPayAsYouOrder.Should().BeFalse();
    branch.SupportsBillOut.Should().BeFalse();
}

[Fact]
public void ConfigureRestoServiceTypes_sets_both_flags_independently()
{
    var branch = Branch.Create(Guid.NewGuid(), "Main", "MAIN", "123 St", null, "City", "Province", null, null);

    branch.ConfigureRestoServiceTypes(supportsPayAsYouOrder: true, supportsBillOut: false);

    branch.SupportsPayAsYouOrder.Should().BeTrue();
    branch.SupportsBillOut.Should().BeFalse();
}
```

- [ ] **Step 3: Run to confirm it fails to compile**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~BranchTests"`
Expected: build error.

- [ ] **Step 4: Add the flags to `Branch`**

In `src/Negosio.Domain/Entities/Branch.cs`, add two properties (after `IsActive`):
```csharp
public bool SupportsPayAsYouOrder { get; private set; }

public bool SupportsBillOut { get; private set; }
```

Both default to `false` — no constructor change needed, since C# bools default to `false` and the private constructor doesn't need to set them explicitly (existing convention in this file: `IsActive` IS set explicitly in the constructor because it defaults to `true`, an exception to the type default — `SupportsPayAsYouOrder`/`SupportsBillOut` need no such override since `false` is exactly what's wanted).

Add the mutator (after `Reactivate()`), following `TenantProfile.ConfigureTax`'s naming precedent:
```csharp
public void ConfigureRestoServiceTypes(bool supportsPayAsYouOrder, bool supportsBillOut)
{
    SupportsPayAsYouOrder = supportsPayAsYouOrder;
    SupportsBillOut = supportsBillOut;
    Touch();
}
```

- [ ] **Step 5: Run to confirm it passes**

Run: `dotnet test tests/Negosio.UnitTests --filter "FullyQualifiedName~BranchTests"`
Expected: passing.

- [ ] **Step 6: Add the columns to `BranchConfiguration`**

Read `src/Negosio.Infrastructure/Persistence/Configurations/BranchConfiguration.cs` first to match its exact existing style, then add:
```csharp
builder.Property(b => b.SupportsPayAsYouOrder).IsRequired().HasDefaultValue(false);
builder.Property(b => b.SupportsBillOut).IsRequired().HasDefaultValue(false);
```

- [ ] **Step 7: Build**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Negosio.Domain/Entities/Branch.cs src/Negosio.Infrastructure/Persistence/Configurations/BranchConfiguration.cs tests/Negosio.UnitTests/BranchTests.cs
git commit -m "feat(resto): add Branch RestoPOS service-type flags"
```

---

## Task 11: DbSets, migration, and the filtered-index integration test

**Files:**
- Modify: `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs`
- Create: migration files under `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/` (generated, not hand-written)
- Test: `tests/Negosio.IntegrationTests/Resto/RestoOrderTableOccupancyTests.cs`

**Interfaces:**
- Consumes: every entity from Tasks 2–10.
- Produces: nothing further — this is the plan's final integration point.

- [ ] **Step 1: Register the new DbSets**

In `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs`, add (grouped near the end, after `UserPermissionGrants`, matching the file's existing one-DbSet-per-line style — no new `using` needed, since every Resto entity lives in the same flat `Negosio.Domain.Entities` namespace this file already imports):
```csharp
public DbSet<RestoStation> RestoStations => Set<RestoStation>();

public DbSet<RestoTable> RestoTables => Set<RestoTable>();

public DbSet<RestoOrder> RestoOrders => Set<RestoOrder>();

public DbSet<RestoOrderRound> RestoOrderRounds => Set<RestoOrderRound>();

public DbSet<RestoOrderItem> RestoOrderItems => Set<RestoOrderItem>();

public DbSet<RestoOrderItemModifier> RestoOrderItemModifiers => Set<RestoOrderItemModifier>();

public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();

public DbSet<ModifierOption> ModifierOptions => Set<ModifierOption>();

public DbSet<ProductModifierGroup> ProductModifierGroups => Set<ProductModifierGroup>();
```

- [ ] **Step 2: Build**

Run: `dotnet build Negosio.sln`
Expected: 0 errors.

- [ ] **Step 3: Generate the migration**

Run (from the repo root):
```bash
dotnet tool restore
dotnet dotnet-ef migrations add AddRestoPos --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
```

Expected: three new files under `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/` (the migration, its designer file, and an updated `TenantDbContextModelSnapshot.cs`). Read the generated migration's `Up()` method and confirm it creates all nine new tables plus the two new `Sales`/`Branches` columns, and that the `IX_RestoOrders_TableId_Open` filtered index's SQL matches what `RestoOrderConfiguration.cs` specified.

- [ ] **Step 4: Write the failing integration test**

Real fixture API confirmed by reading `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs`: `RegisterLoginAndAuthorizeAsync()` returns a `LoginResponse` (sets `CurrentTenantId` as a side effect) whose `User.Id`/`User.TenantId` are the owner's id/tenant; `GetMainBranchIdAsync(login)` returns the seeded branch id; `CreateRegisterAsync(branchId)` returns a `RegisterDto` with `.Id`; `OpenSessionAsync(registerId)` returns a `RegisterSessionDto` with `.Id`; `InScopeAsync<T>(Func<TenantDbContext, Task<T>> action)` runs a callback against a real `TenantDbContext` for `CurrentTenantId`.

```csharp
// tests/Negosio.IntegrationTests/Resto/RestoOrderTableOccupancyTests.cs
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Domain.Entities;
using Xunit;

namespace Negosio.IntegrationTests.Resto;

public class RestoOrderTableOccupancyTests : IntegrationTest
{
    public RestoOrderTableOccupancyTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task A_second_open_order_against_the_same_table_is_rejected_at_the_database_level()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);

        var act = () => InScopeAsync(async db =>
        {
            var table = RestoTable.Create(login.User.TenantId, branchId, "Table 1");
            db.RestoTables.Add(table);
            await db.SaveChangesAsync();

            var firstOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(firstOrder);
            await db.SaveChangesAsync();

            var secondOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(secondOrder);
            await db.SaveChangesAsync();
            return true;
        });

        await act.Should().ThrowAsync<DbUpdateException>(
            "the filtered unique index must reject a second Open order on the same table");
    }

    [Fact]
    public async Task A_new_open_order_is_allowed_once_the_first_is_settled()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);

        var secondOrderId = await InScopeAsync(async db =>
        {
            var table = RestoTable.Create(login.User.TenantId, branchId, "Table 2");
            db.RestoTables.Add(table);
            await db.SaveChangesAsync();

            var firstOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(firstOrder);
            await db.SaveChangesAsync();

            firstOrder.Settle(Guid.NewGuid(), DateTime.UtcNow);
            await db.SaveChangesAsync();

            var secondOrder = RestoOrder.OpenBillOut(login.User.TenantId, branchId, session.Id, login.User.Id, table.Id, null);
            db.RestoOrders.Add(secondOrder);
            await db.SaveChangesAsync();
            return secondOrder.Id;
        });

        secondOrderId.Should().NotBeEmpty();
    }
}
```

- [ ] **Step 5: Run to confirm it fails, then apply the migration and confirm it passes**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~RestoOrderTableOccupancyTests"`
Expected: first run fails (migration not yet applied to the test database / tables don't exist — check how this project's integration tests apply migrations, likely automatic per-test-database provisioning; if so this test failing before Step 3 is not meaningful, so run it fresh now that Step 3's migration exists). After the migration is in place: 2/2 passing.

- [ ] **Step 6: Run the full existing test suite once to confirm no regressions**

Run: `dotnet test tests/Negosio.UnitTests`
Expected: all existing tests still pass (this task only adds DbSets and a migration; Tasks 9-10 already confirmed `Sale`/`Branch` compile everywhere).

Run: `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~Resto"`
Expected: 2/2 passing (this task's own new tests only — do not run the full multi-hundred-test integration suite here, that belongs to this plan's own final verification step below).

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs src/Negosio.Infrastructure/Persistence/Migrations/Tenant/ tests/Negosio.IntegrationTests/Resto/RestoOrderTableOccupancyTests.cs
git commit -m "feat(resto): register DbSets and add the RestoPOS migration"
```

---

## Task 12: Full verification pass

**Files:** none (verification only).

- [ ] **Step 1: Run the full backend test suite**

Run: `dotnet test`
Expected: every existing test still passes (this plan added new entities/columns/a migration; it did not modify any existing behavior beyond `Sale.Begin`'s new optional parameter and `Branch`'s two new defaulted-false columns, neither of which should change any existing test's outcome), plus every new test from Tasks 2–11.

- [ ] **Step 2: Confirm no stray schema drift**

Run: `dotnet dotnet-ef migrations list --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api`
Expected: `AddRestoPos` (or whatever name Task 11 used) is the latest migration, with no pending model changes reported.

- [ ] **Step 3: Report results**

Summarize: total test count before/after this plan, confirm every one of the design spec's Section 3 entities exists with the exact shape specified, and flag anything discovered during implementation that the M2 plan (order lifecycle services) should account for.
