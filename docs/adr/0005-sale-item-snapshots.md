# ADR 0005 — SaleItem stores transaction-time snapshots

- Status: Accepted
- Date: 2026-08-29
- Phase: 3 (Retail POS)

## Context

`Product` and `ProductVariant` change over time — a product is renamed, a price goes up, a variant
is deactivated. An old receipt or sales report must still show what was actually sold and charged at
the time, not today's values. Reading old sales through live joins to the catalog would silently
rewrite history.

## Decision

`SaleItem` copies the fields it needs **at checkout time** and never reads them back from the
catalog:

- `ProductNameSnapshot`, `VariantNameSnapshot`, `SkuSnapshot`, `BarcodeSnapshot`
- `UnitPrice` (the `ProductVariant.SellingPrice` used), `Quantity`
- `GrossAmount`, `DiscountAmount`, `TaxAmount`, `NetAmount` (all computed by the backend)
- `DiscountKind` + `DiscountValue` (what the cashier applied)
- `CostPriceSnapshot` — the `ProductVariant.CostPrice` at sale time, for future margin reporting

Receipts, sale detail and returns all read these snapshots. Margin is computed from
`CostPriceSnapshot`, never from the current `ProductVariant.CostPrice`.

`SaleItem.ProductVariantId` is still stored (for grouping / analytics and to link returns), but it
is a reference, not the source of display data.

## Consequences

- Receipts are immutable regardless of later catalog edits.
- `SaleItem` rows are wider, which is the correct trade-off for financial records.
- `CostPriceSnapshot` and any derived margin are redacted from API responses for roles outside
  Owner / Admin / Manager / InventoryStaff (reusing `CatalogAccess.CanViewCost`); cashiers never
  see cost.
