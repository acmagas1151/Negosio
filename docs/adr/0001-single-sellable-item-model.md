# ADR 0001 — Single sellable-item model (Product vs ProductVariant)

- Status: Accepted
- Date: 2026-08-29
- Phase: 2 (Catalog + Inventory)

## Context

Phase 2 introduces Products, Product Variants, SKU and Barcode. The initial spec put
`SKU`, `Barcode`, `CostPrice` and `SellingPrice` directly on `Product`, with `ProductVariant`
as an optional child carrying *nullable price overrides*. That shape forces per-tenant SKU/Barcode
uniqueness to span **two tables** (`Product` and `ProductVariant`), which SQL Server cannot enforce
with a single index. The alternatives were a shared code table (extra writes/joins on every catalog
mutation) or application-only cross-table checks (a race window, and brittle).

## Decision

`Product` is the **catalog-level definition** only: `CategoryId, Name, Description, TrackInventory,
IsActive, HasVariants`. Every product owns **at least one** `ProductVariant` — the *sellable item* —
which carries `Name, Sku?, Barcode?, CostPrice, SellingPrice, IsActive`.

- A product with no user-defined variants gets one auto-created `IsDefault = true` variant. The
  variants API hides it; the product form edits its price/codes inline. `HasVariants` stays `false`.
- Adding the first real variant **promotes** the default row (fills in its fields, clears
  `IsDefault`) and flips `HasVariants` to `true`; further variants are new rows.
- `CostPrice` / `SellingPrice` are **required and non-negative** on every variant (there is nothing
  to "override").
- A product must always keep at least one active variant.
- simple → variant is supported; variant → simple is out of scope for Phase 2.

Because all codes live on `ProductVariant`, per-tenant uniqueness is enforced with two **filtered
unique indexes** on one table:

```
IX_ProductVariants_TenantId_Sku      UNIQUE  WHERE [Sku]     IS NOT NULL
IX_ProductVariants_TenantId_Barcode  UNIQUE  WHERE [Barcode] IS NOT NULL
```

## Consequences

- One table, one index per code — no cross-table race, no shared code table.
- `BranchInventory` and `StockMovement` reference `ProductVariantId` only (see ADR 0002); no
  nullable "product-or-variant" ambiguity.
- `ProductDto` exposes `minSellingPrice` / `maxSellingPrice` (and cost equivalents where the caller
  is allowed to see cost) so multi-variant products present a price range. Price sorting uses the
  **minimum active selling price**.
- The default variant is an implementation detail. The UI still shows simple products as ordinary
  products with a single price and SKU.
