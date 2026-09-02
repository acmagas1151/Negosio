import { useEffect, useRef } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { salesApi } from '../api/pos'
import { PAYMENT_METHOD_LABELS, SALE_STATUS_LABELS } from '../lib/pos'
import type { PaymentMethod } from '../api/types'
import { formatMoney, formatQty } from '../lib/format'
import { Button, ErrorState, LoadingState } from '../components/ui'

const RECEIPT_CSS = `
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

export default function ReceiptPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const autoPrint = params.get('print') === '1'
  const printedRef = useRef(false)

  const query = useQuery({
    queryKey: ['sales', id, 'receipt'],
    queryFn: () => salesApi.receipt(id),
    enabled: !!id,
  })

  useEffect(() => {
    if (!autoPrint || printedRef.current || !query.isSuccess) return
    printedRef.current = true
    const raf = requestAnimationFrame(() => window.print())
    return () => cancelAnimationFrame(raf)
  }, [autoPrint, query.isSuccess])

  if (query.isPending) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <LoadingState />
      </div>
    )
  }
  if (query.isError) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background p-4">
        <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
      </div>
    )
  }

  const r = query.data
  const when = new Date(r.createdAtUtc).toLocaleString()
  const refundedBanner =
    r.status === 'Refunded' || r.status === 'PartiallyRefunded'
      ? SALE_STATUS_LABELS[r.status].toUpperCase()
      : null
  const voidedBanner = r.status === 'Voided' ? 'VOID — SALE CANCELLED' : null

  return (
    <div className="receipt-page">
      <style>{RECEIPT_CSS}</style>
      <div className="receipt">
        <h1>{r.storeName}</h1>
        <p className="center muted">{r.branchName}</p>
        <hr />
        <div className="row">
          <span>Receipt</span>
          <span className="r bold">#{r.saleNumber}</span>
        </div>
        <div className="row">
          <span>Register</span>
          <span className="r">{r.registerName}</span>
        </div>
        <div className="row">
          <span>Cashier</span>
          <span className="r">{r.cashierName}</span>
        </div>
        <div className="row">
          <span>Date</span>
          <span className="r">{when}</span>
        </div>
        <hr />
        {r.lines.map((line, i) => (
          <div className="item" key={i}>
            <div>
              {line.description}
              {line.variantName ? ` (${line.variantName})` : ''}
            </div>
            <div className="row">
              <span className="muted">
                {formatQty(line.quantity)} × {formatMoney(line.unitPrice)}
              </span>
              <span className="r">{formatMoney(line.netAmount)}</span>
            </div>
          </div>
        ))}
        <hr />
        <div className="row">
          <span>Subtotal</span>
          <span className="r">{formatMoney(r.subtotal)}</span>
        </div>
        {r.discountTotal > 0 && (
          <div className="row">
            <span>Discount</span>
            <span className="r">−{formatMoney(r.discountTotal)}</span>
          </div>
        )}
        <div className="row">
          <span>Tax</span>
          <span className="r">{formatMoney(r.taxTotal)}</span>
        </div>
        <div className="row bold">
          <span>TOTAL</span>
          <span className="r">{formatMoney(r.grandTotal)}</span>
        </div>
        <hr />
        {r.payments.map((p, i) => (
          <div className="row" key={i}>
            <span>{PAYMENT_METHOD_LABELS[p.method as PaymentMethod] ?? p.method}</span>
            <span className="r">{formatMoney(p.amount)}</span>
          </div>
        ))}
        <div className="row">
          <span>Change</span>
          <span className="r">{formatMoney(r.changeDue)}</span>
        </div>
        {refundedBanner && <div className="banner">{refundedBanner}</div>}
        {voidedBanner && <div className="banner-void">{voidedBanner}</div>}
        <hr />
        <p className="center muted">Thank you!</p>
      </div>

      <div className="receipt-actions">
        <Button onClick={() => window.print()}>Print</Button>
        <Button variant="secondary" onClick={() => navigate(-1)}>
          Back
        </Button>
      </div>
    </div>
  )
}
