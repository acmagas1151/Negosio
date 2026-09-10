import type { ReceiptWidth } from '../../api/types'

export function thermalReceiptCss(width: ReceiptWidth): string {
  if (width === 'Mm80') {
    return `
.receipt-page { display:flex; flex-direction:column; align-items:center; background:#f3f4f6; min-height:100vh; padding:24px; }
.receipt { width:80mm; background:#fff; color:#000; padding:6mm 4mm; font:12px/1.45 ui-monospace, Menlo, Consolas, monospace; }
.receipt h1 { font-size:14px; text-align:center; margin:0 0 2px; }
.receipt .center { text-align:center; }
.receipt .muted { color:#333; }
.receipt .row { display:flex; justify-content:space-between; gap:8px; }
.receipt .row .r { text-align:right; white-space:nowrap; }
.receipt hr { border:0; border-top:1px dashed #000; margin:6px 0; }
.receipt .item { margin:2px 0; }
.receipt .bold { font-weight:700; }
.receipt .banner { border:1px solid #000; padding:2px 4px; text-align:center; margin:6px 0; font-weight:700; }
.receipt .banner-void { border:2px solid #b91c1c; color:#b91c1c; padding:3px 4px; text-align:center; margin:6px 0; font-weight:700; letter-spacing:0.5px; }
.receipt-actions { margin-top:16px; display:flex; gap:8px; }
@media print {
  .receipt-page { background:#fff; padding:0; display:block; }
  .receipt { width:auto; padding:0; }
  .receipt-actions { display:none !important; }
  @page { margin:4mm; }
}
`
  } else {
    // Mm58
    return `
.receipt-page { display:flex; flex-direction:column; align-items:center; background:#f3f4f6; min-height:100vh; padding:24px; }
.receipt { width:58mm; background:#fff; color:#000; padding:6mm 4mm; font:11px/1.45 ui-monospace, Menlo, Consolas, monospace; }
.receipt h1 { font-size:14px; text-align:center; margin:0 0 2px; }
.receipt .center { text-align:center; }
.receipt .muted { color:#333; }
.receipt .row { display:flex; justify-content:space-between; gap:8px; }
.receipt .row .r { text-align:right; white-space:nowrap; }
.receipt hr { border:0; border-top:1px dashed #000; margin:6px 0; }
.receipt .item { margin:2px 0; }
.receipt .bold { font-weight:700; }
.receipt .banner { border:1px solid #000; padding:2px 4px; text-align:center; margin:6px 0; font-weight:700; }
.receipt .banner-void { border:2px solid #b91c1c; color:#b91c1c; padding:3px 4px; text-align:center; margin:6px 0; font-weight:700; letter-spacing:0.5px; }
.receipt-actions { margin-top:16px; display:flex; gap:8px; }
@media print {
  .receipt-page { background:#fff; padding:0; display:block; }
  .receipt { width:auto; padding:0; }
  .receipt-actions { display:none !important; }
  @page { margin:4mm; }
}
`
  }
}
