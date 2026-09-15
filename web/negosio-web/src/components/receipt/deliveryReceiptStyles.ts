/**
 * A4 print stylesheet for the persistent fulfillment print page — shared by the Delivery Receipt
 * and the Pickup Slip (DeliveryReceiptPage.tsx renders both from one component, driven by
 * `dto.method`; the two documents differ only in which text/blocks are present, not in layout,
 * so a single class set covers both).
 * Mirrors the shape of `thermalReceiptCss` (a function returning a self-contained CSS string
 * injected via an inline <style>), but this is A4-only — do NOT reuse the thermal sheet.
 */
export function deliveryReceiptCss(): string {
  return `
.dr-page { display:flex; flex-direction:column; align-items:center; background:#f3f4f6; min-height:100vh; padding:24px; }
.dr { width:190mm; margin:0 auto; background:#fff; color:#000; padding:16mm 12mm; font:12px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
.dr h1 { font-size:18px; text-align:center; margin:0 0 2px; }
.dr .center { text-align:center; }
.dr .bold { font-weight:700; }
.dr .muted { color:#333; }
.dr p { margin:0; }
.dr-title { text-align:center; font-size:15px; font-weight:700; letter-spacing:1px; margin:6px 0 0; }
.dr hr { border:0; border-top:1px solid #000; margin:10px 0; }
.dr-meta { display:flex; justify-content:space-between; gap:12px; margin:4px 0; }
.dr-recipient { margin:10px 0; }
.dr-line { margin:2px 0; overflow-wrap:anywhere; }
.dr-line .label { font-weight:600; }
table.dr-items { width:100%; border-collapse:collapse; margin:12px 0; }
table.dr-items th, table.dr-items td { border:1px solid #000; padding:6px 8px; text-align:left; vertical-align:top; }
table.dr-items th.num, table.dr-items td.num { text-align:right; white-space:nowrap; }
.dr-notes { margin:12px 0; }
.dr-notes .label { font-weight:600; }
.dr-signatures { margin:20px 0 0; }
.dr-sig { margin:0 0 22px; }
.dr-sig .name { font-weight:600; }
.dr-sig-rule { display:block; border-bottom:1px solid #000; height:20px; max-width:80mm; margin-top:6px; }
.dr-actions { margin-top:16px; display:flex; gap:8px; }
@media print {
  .dr-page { background:#fff; padding:0; display:block; }
  .dr { width:auto; padding:0; }
  .dr-actions { display:none !important; }
  @page { size:A4; margin:12mm; }
}
`
}
